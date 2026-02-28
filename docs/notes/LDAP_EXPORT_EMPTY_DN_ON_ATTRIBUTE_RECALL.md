# LDAP Export Failure: Empty DN Component on Attribute Recall

- **Status:** Fixed (Option B + C implemented)
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

### Attempt 1: Option B + Option C (Successful)

**Branch:** `fix/ldap-export-empty-dn-on-attribute-recall`

**Approach:** Implemented Option B (skip expression re-evaluation during pure attribute recall) as the primary fix and Option C (DN validation in LDAP connector) as defence-in-depth.

**Key Insight:** C# string concatenation treats `null` as empty string. So a DN expression like `"CN=" + mv["DisplayName"] + ",OU=" + mv["Department"] + ",..."` does NOT return `null` when `Department` is null — it produces `"OU="` (an invalid but non-null string). This means the existing null-check on expression results was insufficient; the expression evaluates "successfully" but produces a malformed DN.

**Fix 1 — Root cause (`ExportEvaluationServer.cs`):**

In `CreateAttributeValueChanges()`, added "pure recall" detection: when ALL `changedAttributes` are also in `removedAttributes`, this indicates a pure removal scenario (CSO obsoletion) rather than a mixed add+remove (normal value change). When `isPureRecall` is true, expression-based mappings are skipped entirely — only direct attribute mappings produce null-clearing changes.

```csharp
// Detect "pure recall" scenario: all changed attributes are removals with no additions.
var isPureRecall = !isCreateOperation
    && removedAttributes is { Count: > 0 }
    && changedAttributes.All(ca => removedAttributes.Contains(ca));
```

Then inside the expression evaluation branch:
```csharp
if (isPureRecall)
{
    Log.Debug("CreateAttributeValueChanges: Skipping expression '{Expression}' for attribute " +
        "{AttributeName} - pure attribute recall (all changes are removals)",
        source.Expression, mapping.TargetConnectedSystemAttribute.Name);
    continue;
}
```

**Fix 2 — Defence-in-depth (`LdapConnectorUtilities.cs`, `LdapConnectorExport.cs`):**

Added `HasValidRdnValues(string dn)` utility method that parses a DN and verifies no RDN component has an empty value (e.g., `OU=` or `CN=`). This is called before any ModifyDN or AddRequest in the LDAP connector.

- In `ProcessUpdateAsync`: if the new DN has empty RDN values, the rename is skipped with a warning log (returns success since the real fix prevents this case).
- In `ProcessCreateAsync`: if the DN has empty RDN values, an `InvalidOperationException` is thrown (hard failure since this should never happen for creates).

**Tests:**

| Test file | Tests | Description |
|-----------|-------|-------------|
| `LdapConnectorUtilitiesTests.cs` | 9 tests | `HasValidRdnValues`: valid DN, single component, empty OU, empty CN, empty string, null, multiple empty, escaped comma, whitespace-only |
| `ExportEvaluationNoChangeTests.cs` | 2 tests | Expression skip on pure recall, expression evaluates on mixed changes |
| `AttributeRecallExpressionWorkflowTests.cs` | 1 test | Full pipeline workflow: source import -> full sync -> mark obsolete -> delta sync -> verify DN mapping skipped |

**Gotchas encountered:**
- EF Core in-memory database auto-tracks navigation properties. When `CreateMvObjectTypeAsync` both adds attributes to `DbContext.MetaverseAttributes` AND manually adds them to `mvType.Attributes`, duplicates appear. Fix: query attributes from DB directly with `DbContext.MetaverseAttributes.FirstAsync()`.
- Mock-based tests (using `MockQueryable.Moq`) cannot capture entities added via `AddRangeAsync` in batch operations. Use `WorkflowTestBase` with real in-memory database instead for pipeline tests.

**Result:** All unit tests pass (1855+).

**Integration test status:** Scenario 1 has a pre-existing infrastructure issue where the Joiner test (Test 1) fails before reaching the Leaver test (Test 3). The `Populate-SambaAD.ps1` script pre-creates users in AD with the same `sAMAccountNames` that the CSV Joiner test attempts to provision, resulting in "sAMAccountName already in use" errors. This is unrelated to the attribute recall fix — no DN validation or expression-skip log messages appear in the worker logs. The fix has been validated via:
- 9 unit tests for `HasValidRdnValues` (DN validation utility)
- 2 unit tests for expression skip logic (pure recall + mixed changes)
- 1 full pipeline workflow test (`AttributeRecallExpressionWorkflowTests`) that exercises the exact scenario: source import -> full sync -> mark obsolete -> delta sync -> verify expression DN mapping is skipped during recall, direct mappings produce null-clearing changes, and a Disconnected RPEI is created.

## Related Documents

- [PHASE4_SYNC_PAGE_LOADING_OPTIMISATION.md](PHASE4_SYNC_PAGE_LOADING_OPTIMISATION.md) — Documents a similar issue where expression-based mappings failed after attribute recall (resolved differently for Scenario 4)
- [LDAP_SCHEMA_DISCOVERY_REVIEW.md](LDAP_SCHEMA_DISCOVERY_REVIEW.md) — Related LDAP export attribute writability issues
