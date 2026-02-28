# LDAP Export Failure: Empty DN Component on Attribute Recall

- **Status:** Fixed (Option C only — defence-in-depth DN validation)
- **Severity:** High - blocks leaver (deprovisioning) scenario
- **First observed:** Integration test Scenario 1, Test 3 (Leaver)
- **Occurrences:** Reproduced multiple times
- **Fixed in:** Branch `fix/ldap-export-empty-dn-on-attribute-recall`

## Symptom

LDAP export fails during the leaver scenario with:

```
System.DirectoryServices.Protocols.DirectoryOperationException:
The distinguished name contains invalid syntax.
Empty RDN value on OU=,OU=Users,OU=Corp,DC=subatomic,DC=local not permitted!
```

The export is an **Update** operation (not a Delete), and it attempts a ModifyDN (rename/move) with an invalid target DN containing an empty `OU=` component.

**Log excerpt (worker):**

```
[ERR] LdapConnectorExport.ExecuteAsync: Failed to process pending export
      547a7773-... (Update)
[WRN] MarkExportFailed: Export 547a7773-... failed (attempt 1/5).
      Error: The distinguished name contains invalid syntax.
      Empty RDN value on OU=,OU=Users,OU=Corp,DC=subatomic,DC=local not permitted!
```

## Root Cause

When a user is removed from the CSV source (leaver scenario), the following sequence occurs:

```
CSV Full Import
  --> CSV CSO marked as Obsolete (user no longer in file)

CSV Delta Sync
  --> ProcessObsoleteConnectedSystemObjectAsync()
      --> Attribute recall: department, title, etc. removed from MVO
      --> MVO queued for export evaluation with removed attributes
  --> EvaluatePendingExportsAsync()
      --> EvaluateOutboundExportsAsync() called for the MVO
          --> Step 1: EvaluateExportRulesWithNoNetChangeDetectionAsync()
              --> MVO is still "in scope" (no scoping filter, type still matches)
              --> Creates UPDATE PendingExport with attribute changes
              --> DN expression evaluates with null department:
                  "CN=User,OU=,OU=Users,OU=Corp,DC=subatomic,DC=local"
                                 ^^ empty!
          --> Step 2: EvaluateOutOfScopeExportsAsync()
              --> MVO is still in scope (no scoping filter) --> no-op

LDAP Export
  --> ProcessUpdateAsync() picks up the Update PE
  --> GetNewDistinguishedName() returns the invalid DN
  --> ProcessRenameAsync() sends ModifyDNRequest with empty OU=
  --> LDAP server rejects: "Empty RDN value not permitted!"
```

**The fundamental issue:** The export evaluation creates an Update PE with recalled (now-null) attributes, and the DN expression evaluates those null values into an invalid DN. The system doesn't recognise that the MVO is being deprovisioned — it sees attribute changes and creates an Update export, not a Delete export.

## Why No Delete PE Is Created

The deprovisioning path (`EvaluateOutOfScopeExportsAsync`) only creates a Delete PE when the MVO falls **out of scope** for an export rule. In Scenario 1, the LDAP export rule has no scoping filter — all User-type MVOs are in scope regardless of their attribute values. Therefore the MVO remains "in scope" even after the CSV CSO disconnects and attributes are recalled.

The actual deprovisioning relies on the **MVO deletion rule** (WhenLastConnectorDisconnected with 7-day grace period). But the LDAP CSO is still connected — only the CSV CSO was disconnected. So:

- The MVO is not being deleted (grace period hasn't started for the LDAP CSO)
- The MVO is still in scope for the LDAP export rule
- Therefore, recalled attributes generate an Update export, not a Delete export

## Key Code Locations

| File | Line | Method | Role |
|------|------|--------|------|
| `SyncTaskProcessorBase.cs` | 564 | `ProcessObsoleteConnectedSystemObjectAsync` | Recalls attributes from disconnected CSO |
| `SyncTaskProcessorBase.cs` | 599 | (same) | Queues MVO for export evaluation with recalled attrs |
| `SyncTaskProcessorBase.cs` | 964 | `EvaluateOutboundExportsAsync` | Calls in-scope then out-of-scope evaluation |
| `ExportEvaluationServer.cs` | 373 | `EvaluateExportRulesWithNoNetChangeDetectionAsync` | Creates Update PE (MVO still in scope) |
| `ExportEvaluationServer.cs` | 449 | `EvaluateOutOfScopeExportsAsync` | No-op (MVO still in scope) |
| `LdapConnectorExport.cs` | 624 | `ProcessUpdateAsync` | Detects DN change, calls rename |
| `LdapConnectorExport.cs` | 667 | `ProcessRenameAsync` | Sends ModifyDNRequest, fails |

## Analysis: Why the Wrong Operation Type Is Generated

The core problem is an **operation priority issue**. When the CSV source CSO disconnects (leaver), two things happen in `ProcessObsoleteConnectedSystemObjectAsync`:

1. Attributes contributed by the CSV system are recalled (removed from MVO)
2. The MVO is queued for export evaluation with those removed attributes

When export evaluation runs (`EvaluateOutboundExportsAsync`):

- **Step 1** (`EvaluateExportRulesWithNoNetChangeDetectionAsync`): The MVO is still "in scope" for the LDAP export rule (no scoping filter, type still matches). So it creates an **Update** PE — treating the attribute recall as a normal attribute change. The DN expression re-evaluates against the now-incomplete MVO (null department) and produces `OU=,OU=Users,...`.
- **Step 2** (`EvaluateOutOfScopeExportsAsync`): The MVO is still in scope — no-op.

Meanwhile, `ProcessMvoDeletionRuleAsync` runs with `WhenLastConnectorDisconnected` and a 7-day grace period. Since the LDAP CSO is still connected (`remainingCsoCount > 0`), no Delete PE is created. The MVO isn't being deleted yet.

**The result:** An Update PE with an invalid DN reaches the LDAP connector. It should never have been created — the system should recognise that a source disconnection with attribute recall means the Update PE is based on incomplete/invalid MVO state and should not be actioned.

## Recommended Solutions

### Option A (Recommended): Deprovisioning Exports Supersede Update Exports

**Principle:** When the system determines that an MVO is being deprovisioned (source CSO disconnected), any Update pending exports for that MVO to target systems should be superseded. The deprovisioning outcome (Delete, Disconnect, or no action depending on config) takes priority over attribute-change-driven Updates that are now based on incomplete MVO state.

**Where:** `SyncTaskProcessorBase.cs`, in `ProcessObsoleteConnectedSystemObjectAsync()` and/or `EvaluateOutboundExportsAsync()`.

**What:**
1. When a source CSO disconnects and attributes are recalled, check whether the deprovisioning path will generate a Delete PE (based on `OutboundDeprovisionAction`, deletion rules, grace period, remaining CSO count).
2. If a Delete PE will be created: skip export evaluation for the recalled attributes entirely — the Delete PE supersedes any attribute-level Updates.
3. If a Delete PE will NOT be created (e.g., `OutboundDeprovisionAction=Disconnect`, or grace period active with remaining CSOs): still skip expression-based re-evaluation (DN, userAccountControl, accountExpires) during the recall, because the MVO state is now incomplete and expression outputs will be invalid. Only propagate direct attribute removals (e.g., clear `department` in the target).
4. Remove any pre-existing Update PEs for the same CSO that are now superseded by the deprovisioning outcome.

**Pros:**
- Addresses the root cause: deprovisioning takes priority over attribute-change-driven updates
- Prevents all expression re-evaluation against incomplete MVO state, not just DN
- Correctly handles both Delete and Disconnect deprovisioning actions
- Aligns with the principle that a higher-level operation (deprovisioning) should invalidate lower-level operations (attribute updates)

**Cons:**
- Requires careful coordination between the obsolete CSO processing and export evaluation paths
- Must correctly handle edge cases (e.g., what if only some attributes are recalled but others remain valid)

### Option B: Skip Expression Re-evaluation on Attribute Removal

**Where:** `ExportEvaluationServer.cs`, in `CreateAttributeValueChanges()`.

**What:** When processing `removedAttributes`, skip expression-based mappings (like DN, userAccountControl, accountExpires) and only generate DELETE/REMOVE changes for the directly-mapped recalled attribute values. Expressions derive their values from other MVO attributes — if those source attributes are being removed, the expression should not be re-evaluated against the now-incomplete state.

**Pros:**
- Simpler than Option A — contained within `CreateAttributeValueChanges()`
- The `removedAttributes` parameter is already threaded through the pipeline
- Semantically correct: a recall means "remove this value", not "recalculate derived values"

**Cons:**
- Narrower fix — only prevents expression re-evaluation, doesn't address the broader priority question
- Update PE is still created (just without the expression-based attribute changes)
- Doesn't handle the case where a pre-existing Update PE already has the invalid DN

### Option C: Validate DN in LDAP Connector (Defence-in-Depth)

**Where:** `LdapConnectorExport.cs`, in `ProcessRenameAsync()` or `ProcessUpdateAsync()`.

**What:** Before sending the ModifyDNRequest, validate that the new DN doesn't contain empty RDN components (e.g., `OU=,`). If invalid, return a clear error result rather than sending the request to the LDAP server.

**Pros:**
- Simple, targeted safety net
- Prevents the LDAP server from receiving invalid requests regardless of how they're generated
- Clear, actionable error message for administrators

**Cons:**
- Doesn't fix the root cause — export still fails, just with a better error
- Should be implemented alongside Option A or B, not as a standalone fix

## Recommended Approach

**Option A** is the correct architectural fix. The principle is clear: when a source CSO disconnects and triggers deprovisioning, that deprovisioning decision should take priority over any Update exports generated from the attribute recall. Update PEs based on incomplete MVO state should be invalidated, not sent to target systems.

**Option C** should also be implemented as defence-in-depth, regardless of which primary fix is chosen — invalid DNs should never reach the LDAP server.

## Reproduction Steps

1. Run integration tests: `./test/integration/Run-IntegrationTests.ps1`
2. Scenario 1, Test 3 (Leaver) will fail at the LDAP Export step
3. Check worker logs: `docker compose logs jim.worker --tail=500 | grep -A5 "Empty RDN"`

## Fix Attempts

### Attempt 1: Option B + Option C (Superseded)

**Approach:** Implemented Option B (skip expression re-evaluation during "pure attribute recall") as the primary fix and Option C (DN validation in LDAP connector) as defence-in-depth.

**Outcome:** Option B was **removed** after review — it violated separation of concerns and was based on a non-representative test scenario. See Attempt 2 for the reasoning and final fix.

### Attempt 2: Option C Only — Defence-in-Depth (Final)

**Branch:** `fix/ldap-export-empty-dn-on-attribute-recall`

**Key Insight:** The original bug scenario (single-source CS with attribute recall enabled, whose attributes feed DN expressions) is a **misconfiguration**. In a properly configured system:

- **Primary sources** (e.g., HR) contribute identity-critical attributes that feed expressions (DN, userAccountControl, etc.). Attribute recall should be **disabled** on these — their disconnection triggers deprovisioning, not recall.
- **Supplemental sources** (e.g., Training) contribute non-critical attributes. Attribute recall is **enabled** on these — their attributes don't feed expressions, so recall produces valid null-clearing exports without breaking DN generation.

**Why Option B was removed:**

1. **Separation of concerns violation**: The `isPureRecall` detection in `ExportEvaluationServer` made the class aware of "recall" semantics — a worker-level concept. A removal is a removal regardless of cause; the class shouldn't detect or special-case specific upstream scenarios.
2. **Suppressing admin intent**: Admins may intentionally write expressions that handle null values for their business logic. It's not for the system to decide that expressions shouldn't be evaluated during attribute removal.
3. **Non-representative test scenario**: The test used a single-source topology where the primary source had attribute recall enabled — this doesn't match real-world deployment patterns.

**What remains — Fix (Option C): DN validation in LDAP connector**

Added `HasValidRdnValues(string dn)` utility method using the existing `DNParser` package (`CPI.DirectoryServices.DN`) to validate that no RDN component has an empty value. This is called before any ModifyDN or AddRequest in the LDAP connector.

- In `ProcessUpdateAsync`: if the new DN has empty RDN values, returns `ExportResult.Failed(...)` so the error is visible to admins in the activity as a failed export object.
- In `ProcessCreateAsync`: if the DN has empty RDN values, throws `InvalidOperationException` (hard failure since this should never happen for creates).

This ensures that if an admin misconfigures attribute recall on a primary source, the resulting invalid DN is caught before reaching the LDAP server, and the failure is reported back to the worker for recording as a visible error in the activity.

**Tests:**

| Test file | Tests | Description |
|-----------|-------|-------------|
| `LdapConnectorUtilitiesTests.cs` | 9 tests | `HasValidRdnValues`: valid DN, single component, empty OU, empty CN, empty string, null, multiple empty, escaped comma, whitespace-only |
| `AttributeRecallExpressionWorkflowTests.cs` | 1 test | Representative multi-source workflow: HR import + Training import → Full Sync → mark Training obsolete → Delta Sync → verify Training attributes cleared, DN expression evaluates correctly (HR attributes retained), no-net-change detection filters unchanged DN |

**Gotchas encountered:**
- EF Core in-memory database auto-tracks navigation properties. When `CreateMvObjectTypeAsync` both adds attributes to `DbContext.MetaverseAttributes` AND manually adds them to `mvType.Attributes`, duplicates appear. Fix: query attributes from DB directly with `DbContext.MetaverseAttributes.FirstAsync()`.
- EF Core in-memory database does not support `EF.Functions.ILike()` (PostgreSQL-specific). Object matching rules in workflow tests must use `CaseSensitive = true` to avoid ILike.

**Result:** All unit tests pass (1783).

## Related Documents

- [PHASE4_SYNC_PAGE_LOADING_OPTIMISATION.md](PHASE4_SYNC_PAGE_LOADING_OPTIMISATION.md) — Documents a similar issue where expression-based mappings failed after attribute recall (resolved differently for Scenario 4)
- [LDAP_SCHEMA_DISCOVERY_REVIEW.md](LDAP_SCHEMA_DISCOVERY_REVIEW.md) — Related LDAP export attribute writability issues
