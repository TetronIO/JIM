# Scenario 4 Test 4: MVO deleted immediately despite 1-minute grace period

> Engineering note — investigation handover. Root cause not yet confirmed; this document records everything learned so another agent can pick up without re-tracing.

## 2026-04-20 follow-up: could not reproduce

Ran Scenario 4 on `feature/integration-test-error-detection` @ bae58cd8 against a clean SambaAD stack four times at `-LogLevel Information`:

| Template | Step | Result | Log |
|----------|------|--------|-----|
| Nano | `-Step AuthoritativeGracePeriod` only | PASS | [Scenario4-DeletionRules-Nano-2026-04-20_073914.log](../../test/integration/results/logs/Scenario4-DeletionRules-Nano-2026-04-20_073914.log) |
| Nano | Full scenario (Tests 1-7) | PASS | [Scenario4-DeletionRules-Nano-2026-04-20_084749.log](../../test/integration/results/logs/Scenario4-DeletionRules-Nano-2026-04-20_084749.log) |
| Medium | Full scenario | PASS | [Scenario4-DeletionRules-Medium-2026-04-20_085924.log](../../test/integration/results/logs/Scenario4-DeletionRules-Medium-2026-04-20_085924.log) |
| Large | Full scenario | PASS | [Scenario4-DeletionRules-Large-2026-04-20_091024.log](../../test/integration/results/logs/Scenario4-DeletionRules-Large-2026-04-20_091024.log) |

At Large specifically — the template where the failure was originally reported — both Test 4 assertions now pass: `MVO still exists (grace period not yet elapsed)` and `MVO deleted after grace period elapsed (housekeeping processed it)`. `errors-*.log` for all four runs is empty.

### Code review conclusions while attempting to reproduce

- **Hypothesis #1 (stale `mvo.Type.DeletionGracePeriod`)** — unlikely by construction. Each sync task gets a fresh `JimApplication` transient ([src/JIM.Worker/Program.cs:66](../../src/JIM.Worker/Program.cs#L66)) with a fresh `DbContext` from `IDbContextFactory` ([src/JIM.Worker/Program.cs:30](../../src/JIM.Worker/Program.cs#L30)). `LoadSyncRules` runs at the start of every sync task ([src/JIM.Worker/Processors/SyncFullSyncTaskProcessor.cs:66](../../src/JIM.Worker/Processors/SyncFullSyncTaskProcessor.cs#L66)), pulling a fresh `MetaverseObjectType` via `.Include(sr => sr.MetaverseObjectType)`. The MVO's `Type` navigation is loaded with `.Include(mvo => mvo.Type)` in the same fresh context, so EF's identity map unifies them to the freshly-read values.
- **Stale `Type` on the long-lived `mainLoopJim`** (housekeeping path) — considered and ruled out. The long-lived DbContext at [src/JIM.Worker/Worker.cs:93](../../src/JIM.Worker/Worker.cs#L93) could theoretically cache a stale tracked `MetaverseObjectType` instance across API updates, but the eligibility WHERE clause at [MetaverseRepository.cs:1518-1520](../../src/JIM.PostgresData/Repositories/MetaverseRepository.cs#L1518-L1520) evaluates `DeletionGracePeriod` server-side. A stale in-memory Type instance cannot fool the SQL filter.
- **Hypothesis #3 (`Test-MvoExists` false negative on display name)** — not applicable: Test 4 runs with `RemoveContributedAttributesOnObsoletion=false`, so the display name is retained during the grace period.
- **Hypothesis #4 (swallowed exception in DB-update path)** — no supporting evidence; `errors-*.log` is empty in every re-run.

### Working theory

The original Large-template failure was most likely a **timing artefact in the full pre-release regression** (long-running, concurrent activities against Samba AD) rather than a deterministic product bug. If a future regression hits the same assertion, the cheapest next step is capturing the worker log at `Information` level around the Test 4 timestamp: the `EvaluateMvoDeletionRule:` and `MarkMvoForDeletionAsync:` lines deterministically distinguish "decision was wrong" (stale-type bug) from "decision was right, test raced housekeeping" (timing).

A small belt-and-braces improvement is still worth considering: add an `Assert-MvoExistsById` fallback in Test 4 Assert 1 so a future failure immediately rules out hypothesis #3.

---

## Original investigation notes (pre-2026-04-20)

## Symptom

`Invoke-Scenario4-DeletionRules.ps1` Test 4 ("WhenAuthoritativeSourceDisconnected + 1-Minute Grace Period") asserts that the MVO **still exists** three seconds after the authoritative CSV source disconnects — because the 1-minute grace period has not yet elapsed. The assertion fails:

```
Scenario 4 failed: Test 4 Assert 1 failed:
  MVO was deleted immediately despite 1-minute grace period
```

The failure has been observed at `Large` template in a full pre-release SambaAD regression run. The same scenario at `Micro` template in an earlier run passed. The failing assertion is at [test/integration/scenarios/Invoke-Scenario4-DeletionRules.ps1:1050](../../test/integration/scenarios/Invoke-Scenario4-DeletionRules.ps1#L1050).

## Test flow

1. `Set-DeletionRuleConfig` (line 1023) — calls `Set-JIMMetaverseObjectType` to set `DeletionRule = WhenAuthoritativeSourceDisconnected`, `DeletionGracePeriod = TimeSpan.FromMinutes(1)`, `DeletionTriggerConnectedSystemIds = <CSV>`, `RemoveContributedAttributesOnObsoletion = false`.
2. `Invoke-ProvisionUser` — creates user `test.auth.grace` (employeeId `AUTH002`) via full sync cycle.
3. `Invoke-RemoveUserFromSource` — removes the user from the CSV file and runs CSV import + sync only (no LDAP sync).
4. `Start-Sleep -Seconds 3`.
5. **Assert 1:** `Test-MvoExists -DisplayName "Test Auth Grace"` returns `$false` → test fails.

Expected: MVO still exists, with `isPendingDeletion=true` and `LastConnectorDisconnectedDate` set.

## What is known

### API / DB persistence path

- PowerShell cmdlet `Set-JIMMetaverseObjectType` serialises `DeletionGracePeriod` to the API body as `d.hh:mm:ss` — see [src/JIM.PowerShell/Public/Metaverse/Set-JIMMetaverseObjectType.ps1:138](../../src/JIM.PowerShell/Public/Metaverse/Set-JIMMetaverseObjectType.ps1#L138).
- The API controller at [src/JIM.Web/Controllers/Api/MetaverseController.cs:105-109](../../src/JIM.Web/Controllers/Api/MetaverseController.cs#L105-L109) coerces `TimeSpan.Zero` to `null` but stores any other value as-is. `TimeSpan.FromMinutes(1)` lands as `00:01:00`.
- `MetaverseObjectType.DeletionGracePeriod` is `TimeSpan?` backed by a PostgreSQL `interval NULL` column. No known write-path issues.

### Sync-engine deletion evaluation

- Entry point: [SyncTaskProcessorBase.cs:683 ProcessMvoDeletionRuleAsync](../../src/JIM.Worker/Processors/SyncTaskProcessorBase.cs#L683).
- Calls [SyncEngine.EvaluateMvoDeletionRule](../../src/JIM.Application/Servers/SyncEngine.cs#L151), which — for `WhenAuthoritativeSourceDisconnected` with the disconnecting system in the trigger list — calls [EvaluateGracePeriod](../../src/JIM.Application/Servers/SyncEngine.cs#L261).
- `EvaluateGracePeriod` reads `mvo.Type!.DeletionGracePeriod` and returns `DeleteImmediately` if null/Zero, `ScheduleDeletion(gracePeriod, reason)` otherwise.
- `ApplyMvoDeletionDecisionAsync` ([SyncTaskProcessorBase.cs:845](../../src/JIM.Worker/Processors/SyncTaskProcessorBase.cs#L845)) routes both `DeletedImmediately` and `DeletionScheduled` to `MarkMvoForDeletionAsync`.
- `MarkMvoForDeletionAsync` at [SyncTaskProcessorBase.cs:870](../../src/JIM.Worker/Processors/SyncTaskProcessorBase.cs#L870) **re-reads** `mvo.Type!.DeletionGracePeriod` and branches on `HasValue && != Zero`:
  - **Null or Zero** → adds to `_pendingMvoDeletions` for synchronous deletion at page flush (this is the bad path for Test 4).
  - **> Zero** → sets `LastConnectorDisconnectedDate = UtcNow` and persists via `_syncRepo.UpdateMetaverseObjectAsync(mvo)` (this is the expected path).

### Housekeeping

- [Worker.cs:570 PerformHousekeepingAsync](../../src/JIM.Worker/Worker.cs#L570) runs at most every 60 seconds.
- Eligibility query [MetaverseRepository.cs:1494 GetMetaverseObjectsEligibleForDeletionAsync](../../src/JIM.PostgresData/Repositories/MetaverseRepository.cs#L1494) correctly gates on `LastConnectorDisconnectedDate + DeletionGracePeriod <= now`, so a 1-minute grace period cannot be collected by housekeeping within 3 seconds of disconnection.
- **But** — the same query also treats `DeletionGracePeriod == null` as immediately eligible. If the MVO's type somehow has null grace period at housekeeping time, the MVO is deleted the moment housekeeping runs.

## Hypotheses, ranked

### 1. `MarkMvoForDeletionAsync` sees `mvo.Type.DeletionGracePeriod == null` despite the API having set it

Most likely. The sync-processing context may hold a stale `MetaverseObjectType` that was loaded before the API call persisted the new grace period. EF Core tracking behaviour across long-lived worker scopes is the obvious suspect.

Evidence pointers:
- `SyncTaskProcessorBase.cs` lines 1992–1999 explicitly document that `MetaverseObjectType` is loaded alongside MVOs via the CSO Include chain and that multiple MVOs share in-memory `Type` instances. Shared-instance caching + an out-of-band type update is exactly the shape of stale-navigation-property bugs.
- `src/CLAUDE.md` has an entire section "Prefer FK Scalars Over Navigation Checks Under AsNoTracking" warning about this class of bug. The deletion evaluation uses `mvo.Type.DeletionGracePeriod` — a **column** on the Type entity, not just a navigation check — so the scalar-vs-navigation advice doesn't directly apply, but the underlying loading-semantics concern is the same.

### 2. A race between provisioning and config update

If `Invoke-ProvisionUser` completes after `Set-DeletionRuleConfig` *returns* but before the grace-period update is flushed to the DB (or cached in some middle layer), the provisioning sync might run against a `Type` with null grace period and the deletion path inherits that state via the tracked entity graph.

Less likely than #1 because the API call is synchronous and the DB write happens inside the request handler. But if any caching layer sits between API and worker, this becomes possible.

### 3. Test helper `Test-MvoExists` false negative

`Test-MvoExists` at [Invoke-Scenario4-DeletionRules.ps1:412](../../test/integration/scenarios/Invoke-Scenario4-DeletionRules.ps1#L412) searches by display name. The comment at line 1045 asserts "with recall=false, display name is retained". If something nulls or obscures the display name at disconnection time (e.g. `DisplayName` attribute gets cleared alongside other attributes even when recall=false), the search misses the MVO and the test reports "deleted".

Check: Assert 1 uses `Test-MvoExists` (display-name search); Assert 2 uses `Test-MvoExistsById`. If the MVO actually still exists just with a changed display name, querying by ID would confirm it. The current log only shows Assert 1 failing, not a subsequent ID-based check.

### 4. Timing / real product bug in the DB-update path

`MarkMvoForDeletionAsync` writes `LastConnectorDisconnectedDate` before returning `DeletionScheduled` (line 906). If the write doesn't happen (transaction not committed, exception swallowed), the MVO is left in an in-between state. Less likely — any exception here would show up in RPEIs or the worker log.

## What to check next

1. **Re-run Scenario 4 alone at Nano with `-LogLevel Information`** (do not use `-SkipReset` / `-SkipBuild` per [test/CLAUDE.md](../../test/CLAUDE.md)):

   ```powershell
   ./test/integration/Run-IntegrationTests.ps1 -Scenario Scenario4-DeletionRules -Template Nano -LogLevel Information
   ```

   Grep the worker log for `EvaluateMvoDeletionRule:` and `MarkMvoForDeletionAsync:` lines around the Test 4 timestamp. Two possibilities:
   - Log says `queued for immediate deletion (…). No grace period configured.` → the `Type.DeletionGracePeriod` really is null at that point. This points at hypothesis #1 or #2.
   - Log says `marked for deletion (…). Eligible after 00:01:00.` → decision is correct, but the MVO still disappears. This points at hypothesis #3 or #4.

2. **If hypothesis #1 looks likely,** read the CSO-loading query that materialises `connectedSystemObject.MetaverseObject` for the processing path that leads to `ProcessObsoleteConnectedSystemObjectAsync`. Verify whether the DbContext is long-lived enough to cache a stale `MetaverseObjectType` across an API-triggered config update. If yes, either refresh the type eagerly at the top of the processing loop, or read `DeletionGracePeriod` directly via a lightweight query at decision time rather than through the cached navigation.

3. **If hypothesis #3 looks likely,** change `Test-MvoExists` to also try `Test-MvoExistsById` as a fallback, or have Assert 1 use ID-based lookup exclusively.

4. **Verify `Set-JIMMetaverseObjectType` actually persisted the grace period** by calling `Get-JIMMetaverseObjectType -Id <userObjectTypeId>` immediately after `Set-DeletionRuleConfig` and before `Invoke-ProvisionUser`. Asserting the returned `deletionGracePeriod` equals `00:01:00` would rule out hypothesis #2.

## Why this wasn't caught sooner

- The schema-import failures in earlier runs (now fixed — see commit 81666608) ended Scenarios 4 and 5 before Test 4 ran.
- Earlier Micro regression runs passed Test 4. Possibly by chance of timing, or possibly because the Micro run reached Test 4 with less in-context tracked entity state than the Large run. Reproducing under Nano should narrow this down.

## Scope note

This is **not** a File connector / rootless-base-image problem. It surfaced only because the File-connector issues earlier in the suite are now fixed and Scenario 4 can actually reach Test 4. The sync/deletion/housekeeping interaction is a separate concern and may be a latent product bug that pre-dates the .NET 10 migration.

## Related files

- [src/JIM.Application/Servers/SyncEngine.cs](../../src/JIM.Application/Servers/SyncEngine.cs)
- [src/JIM.Worker/Processors/SyncTaskProcessorBase.cs](../../src/JIM.Worker/Processors/SyncTaskProcessorBase.cs)
- [src/JIM.Worker/Worker.cs](../../src/JIM.Worker/Worker.cs) (housekeeping)
- [src/JIM.PostgresData/Repositories/MetaverseRepository.cs](../../src/JIM.PostgresData/Repositories/MetaverseRepository.cs) (`GetMetaverseObjectsEligibleForDeletionAsync`)
- [src/JIM.Web/Controllers/Api/MetaverseController.cs](../../src/JIM.Web/Controllers/Api/MetaverseController.cs) (config update)
- [test/integration/scenarios/Invoke-Scenario4-DeletionRules.ps1](../../test/integration/scenarios/Invoke-Scenario4-DeletionRules.ps1) (Test 4 begins at `Test-TestSection "Test 4:"` around line 1013)
