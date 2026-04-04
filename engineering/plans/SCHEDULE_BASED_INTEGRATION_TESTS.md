# Schedule-Based Integration Tests

- **Status:** Planned
- **Created:** 2026-03-08

## Overview

Migrate integration test scenarios (1-5, 8) from sequential `Start-JIMRunProfile -Wait` calls to schedule-based execution using `Start-JIMSchedule`. This aligns tests with how customers actually run JIM in production, exercises the scheduler code path end-to-end, and simplifies scenario scripts from verbose step-by-step chains to declarative schedule definitions.

## Problem

Integration tests currently trigger each Run Profile Execution individually:

```powershell
# Scenario 1 Joiner phase — 8 sequential calls, each with polling + assertion
$import = Start-JIMRunProfile -ConnectedSystemId $csvId -RunProfileId $importId -Wait -PassThru
Assert-ActivitySuccess -ActivityId $import.activityId -Name "CSV Import"
$sync = Start-JIMRunProfile -ConnectedSystemId $csvId -RunProfileId $syncId -Wait -PassThru
Assert-ActivitySuccess -ActivityId $sync.activityId -Name "CSV Sync"
# ...6 more steps...
```

This works but has drawbacks:

1. **Doesn't test the scheduler** — the scheduler service, step advancement, and execution orchestration are bypassed entirely in scenarios 1-5, 8
2. **Verbose and repetitive** — each scenario duplicates the same trigger-wait-assert pattern
3. **Diverges from production** — customers use schedules, not individual RPE triggers

## Proposed Solution

### Phase 1: Enhance `Start-JIMSchedule -Wait` for Fast-Fail

**File:** `src/JIM.PowerShell/Public/Schedules/Start-JIMSchedule.ps1`

The current `-Wait` polling loop checks only the top-level execution status. Enhance it to support per-step fail-fast and callbacks:

**New parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `-FailFast` | Switch | During polling, check each step's `activityStatus`. If any step has a failure status (`FailedWithError`, `CompleteWithError`, `Cancelled`) and its schedule step has `ContinueOnFailure=false`, throw immediately with diagnostics |
| `-StepCompleted` | ScriptBlock | Optional callback invoked each time a step reaches terminal status. Receives the step object (with `activityId`, `activityStatus`, `stepIndex`, `name`). Lets callers hook in per-step assertions as steps complete |

**Improved progress display:**
- Show step name and activity status (not just "Step X of Y")
- Show elapsed time
- Show object progress from the activity if available

**Polling behaviour with `-FailFast`:**

```
Poll execution detail endpoint
  |
  +-- For each step with a NEW terminal activityStatus:
  |     +-- Invoke -StepCompleted callback (if provided)
  |     +-- If activityStatus is failure AND ContinueOnFailure=false:
  |           +-- Throw with step name, activityId, activityStatus, errorMessage
  |
  +-- Check overall execution status (Completed/Failed/Cancelled)
  |     +-- If terminal: break loop, return execution
  |
  +-- Check timeout
  +-- Sleep and repeat
```

### Phase 2: Test Helper Functions

**File:** `test/integration/utils/Test-Helpers.ps1`

#### `New-JIMTestSchedule`

Creates a schedule from a compact step definition array. Keeps scenario scripts declarative:

```powershell
$schedule = New-JIMTestSchedule -Name "Joiner Cycle" -Steps @(
    @{ CS = $config.CSVSystemId;  RP = $config.CSVImportProfileId }
    @{ CS = $config.CSVSystemId;  RP = $config.CSVDeltaSyncProfileId }
    @{ CS = $config.LDAPSystemId; RP = $config.LDAPExportProfileId }
    @{ CS = $config.LDAPSystemId; RP = $config.LDAPDeltaImportProfileId }
    @{ CS = $config.LDAPSystemId; RP = $config.LDAPDeltaSyncProfileId }
)
```

Implementation:
1. `New-JIMSchedule -Name $Name -TriggerType Manual -PassThru`
2. For each step: `Add-JIMScheduleStep -ScheduleId ... -StepType RunProfile -ConnectedSystemId ... -RunProfileId ...`
3. Return the schedule with steps

Support an optional `Parallel` flag per step to set `ExecutionMode = ParallelWithPrevious`.

#### `Invoke-ScheduleAndAssert`

High-level helper that combines execution and assertion:

```powershell
$execution = Invoke-ScheduleAndAssert -ScheduleId $schedule.id `
    -Name "Joiner Cycle" `
    -Timeout 600 `
    [-AllowWarnings]
```

Implementation:
1. Call `Start-JIMSchedule -Id $ScheduleId -Wait -FailFast -PassThru` with a `-StepCompleted` callback
2. In the callback: call `Assert-ActivitySuccess` for each completed step (provides fail-fast with full diagnostics)
3. After completion: call `Assert-ScheduleExecutionSuccess` for the overall result
4. Return the execution object (callers can access `$execution.steps[n].activityId` for further assertions like change counts)

### Phase 3: Handle the AD Replication Wait

Scenario 1 has a 5-second wait between LDAP Export and the confirming LDAP Delta Import to allow AD replication. Since schedules execute steps back-to-back, this needs handling.

**Approach: Split into two schedules per phase.**

```powershell
# Schedule A: Import -> Sync -> Export
$scheduleA = New-JIMTestSchedule -Name "Joiner - Sync & Export" -Steps @(
    @{ CS = $config.CSVSystemId;  RP = $config.CSVImportProfileId }
    @{ CS = $config.CSVSystemId;  RP = $config.CSVDeltaSyncProfileId }
    @{ CS = $config.LDAPSystemId; RP = $config.LDAPExportProfileId }
)
$execA = Invoke-ScheduleAndAssert -ScheduleId $scheduleA.id -Name "Joiner - Sync & Export"

# Wait for AD replication
Start-Sleep -Seconds 5

# Schedule B: Confirming Import -> Sync -> Cross-Domain Export -> Import -> Sync
$scheduleB = New-JIMTestSchedule -Name "Joiner - Confirm" -Steps @(
    @{ CS = $config.LDAPSystemId; RP = $config.LDAPDeltaImportProfileId }
    @{ CS = $config.LDAPSystemId; RP = $config.LDAPDeltaSyncProfileId }
    # ...cross-domain steps if configured...
)
$execB = Invoke-ScheduleAndAssert -ScheduleId $scheduleB.id -Name "Joiner - Confirm"
```

This is pragmatic and avoids .NET changes. A `ScheduleStepType.Wait` could be added later as a separate feature if desired.

### Phase 4: Migrate Scenarios

Migrate each scenario to use schedule-based execution. The general pattern per scenario phase:

1. **Define schedule(s)** using `New-JIMTestSchedule` with the same step sequence currently hardcoded
2. **Execute and assert** using `Invoke-ScheduleAndAssert` (fail-fast on any step)
3. **Post-execution assertions** using activity IDs from `$execution.steps`:

```powershell
# After execution, drill into specific step activities for detailed assertions
Assert-ActivityHasChanges -ActivityId $execution.steps[0].activityId -Name "CSV Import" `
    -ExpectedChangeType "Import" -MinExpected $expectedUserCount

Assert-ActivityOutcomeStats -ActivityId $execution.steps[1].activityId -Name "CSV Sync" `
    -TotalObjects $expectedUserCount -TotalCreated $expectedUserCount
```

**Migration order:**

| Order | Scenario | Complexity | Notes |
|-------|----------|-----------|-------|
| 1 | Scenario 1 (HR to AD) | Medium | Template — has AD replication wait, cross-domain conditional steps |
| 2 | Scenario 4 (Deletion Rules) | Low | Simple step sequences |
| 3 | Scenario 5 (Matching Rules) | Low | Simple step sequences |
| 4 | Scenario 2 (Cross-Domain) | Medium | Multiple AD systems |
| 5 | Scenario 8 (Entitlement Sync) | Medium | Multiple AD systems |
| 6 | Scenario 3 (GALSYNC) | N/A | Not yet implemented — skip |

**Per-scenario considerations:**

- **Conditional steps** (e.g., cross-domain only if configured): Build the step array dynamically before passing to `New-JIMTestSchedule`
- **Data modification between phases** (Joiner -> modify CSV -> Mover): Each phase gets its own schedule — this is a natural fit since schedules are per-phase anyway
- **Baseline steps** (initial full import/sync before tests): Keep these as direct `Start-JIMRunProfile` calls — they're one-off setup, not test execution

### Phase 5: Cleanup

1. Remove schedules created during testing (already handled by existing cleanup patterns)
2. Verify Scenario 6 still passes (it tests the scheduler itself — should be unaffected)
3. Update `docs/INTEGRATION_TESTING.md` to document the schedule-based approach

## What Does NOT Change

- **Schedule API/controllers** — already complete, no changes needed
- **Scheduler service** — already works, no changes needed
- **Scenario 6** — already tests scheduler functionality directly, stays as-is
- **`Start-JIMRunProfile`** — still available for one-off baseline/setup steps and debugging
- **`Assert-ActivitySuccess` / `Assert-ActivityHasChanges` / `Assert-ActivityOutcomeStats`** — still used, now called from within the schedule callback instead of inline in scenarios

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Scheduler polling interval (30s) adds latency | Tests take longer | Manual triggers bypass cron — only the step advancement polling adds overhead. The scheduler checks in-progress executions every 30s, but `Start-JIMSchedule -Wait` polls every 5s and the scheduler will advance steps on its next cycle |
| Debugging failures is harder with schedules | Slower dev loop | `-FailFast` + `Assert-ActivitySuccess` callback provides the same diagnostic output as today. `$execution.steps` exposes all activity IDs for manual investigation |
| AD replication wait doesn't fit schedule model | Cannot run full cycle in single schedule | Split into two schedules per phase. Pragmatic and clear |
| Step index correlation | Assertions reference wrong activity | `New-JIMTestSchedule` returns steps in definition order. Step indices are deterministic and match the schedule definition |

## Success Criteria

1. All migrated scenarios pass with schedule-based execution
2. Fail-fast behaviour: a step failure stops the test within one poll cycle (~5s), not after the entire schedule completes
3. Diagnostic output on failure is equivalent to or better than current output
4. Scenario scripts are shorter and more declarative
5. Scheduler service code path is exercised in every integration test run

## Benefits

- **Production parity** — tests exercise the same scheduler code path customers use
- **More code tested** — scheduler step advancement, execution tracking, and activity correlation all get exercised
- **Simpler scenarios** — declarative schedule definitions replace verbose step-by-step chains
- **Reusable helpers** — `New-JIMTestSchedule` and `Invoke-ScheduleAndAssert` benefit future scenarios
- **PS module coverage** — enhanced `Start-JIMSchedule -Wait` with fail-fast is useful beyond testing
