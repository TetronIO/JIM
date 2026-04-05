# Integration Test Full Regression Runner

- **Status:** Done
- **Milestone:** v0.9-STABILISATION
- **Created:** 2026-03-04
- **Issue:** #372

## Overview

Add an `-Scenario All` option to `Run-IntegrationTests.ps1` that runs every implemented integration test scenario in sequence, performing lightweight resets between scenarios. This enables a full regression test in a single invocation without redundant build or infrastructure startup steps.

## Problem

Currently, each integration test scenario must be run individually via `Run-IntegrationTests.ps1 -Scenario "ScenarioX-..."`. Running a full regression suite requires manually invoking the script multiple times, each time paying the cost of:

- Docker image build (~2-3 minutes)
- Samba AD image checks
- Starting all containers from scratch
- Waiting for services to become healthy
- Docker cleanup/prune

For 6 implemented scenarios, this means ~15-20 minutes of redundant infrastructure overhead on top of actual test time.

## Proposed Solution

Modify `Run-IntegrationTests.ps1` to accept `All` as a scenario value. When selected, the runner will:

1. **Build once**: Docker images are built at the start and reused for all scenarios
2. **Start all external systems upfront**: Start samba-ad-primary, samba-ad-source, and samba-ad-target at the beginning (covering all scenario infrastructure requirements)
3. **Loop through each implemented scenario**, performing a lightweight reset between each:
   a. Reset JIM database (remove db volume, restart JIM containers, wait for API health)
   b. Clean Samba AD OUs (delete and recreate test OUs via samba-tool, no container restart)
   c. Generate a fresh API key, update `.env`, restart jim.web
   d. Run scenario setup and tests
   e. Collect per-scenario results
4. **Aggregate results**: Combined pass/fail summary and per-scenario breakdown
5. **Single cleanup pass**: Docker prune and metrics capture once at the end

### Infrastructure Grouping

Scenarios have different external system requirements:

| Group | Scenarios | External Systems |
|-------|-----------|------------------|
| Primary-only | 1, 4, 5, 6 | samba-ad-primary |
| Source+Target | 2, 8 | samba-ad-primary + samba-ad-source + samba-ad-target |
| Not implemented | 3 (GALSYNC) | Skipped automatically |

By starting all Samba AD instances upfront, scenarios that only need the primary instance simply ignore the source/target containers.

### Lightweight Reset Between Scenarios

The key efficiency gain is avoiding full container teardown/rebuild between scenarios. Instead:

1. **JIM reset**: Stop JIM containers, remove JIM database volume, restart JIM containers. This gives a clean database without rebuilding images.
2. **Samba AD cleanup**: Use `samba-tool ou delete --force-subtree-delete` to remove test OUs and `samba-tool user delete` to clean test users. This is much faster than destroying and recreating Samba containers (~2s vs ~60s).
3. **API key rotation**: Generate a new key per scenario to avoid stale authentication state.

### Scenario Execution Order

Scenarios will run in numeric order (1, 2, 4, 5, 6, 8), skipping unimplemented ones (3). The order is not significant since each starts from a clean state.

### Interactive Menu

When `-Scenario` is not specified and the interactive menu is shown, add an `All Scenarios (full regression)` option at the top of the scenario list. Template selection will use the chosen template for all scenarios that support it (scenarios with fixed test data ignore the template parameter as they do today).

### Results Aggregation

The combined results file will include:

```json
{
  "Mode": "FullRegression",
  "Template": "Nano",
  "StartTime": "...",
  "Duration": "...",
  "OverallSuccess": true,
  "Scenarios": [
    {
      "Name": "Scenario1-HRToIdentityDirectory",
      "Success": true,
      "Duration": "00:02:15",
      "ExitCode": 0
    },
    {
      "Name": "Scenario4-DeletionRules",
      "Success": true,
      "Duration": "00:03:42",
      "ExitCode": 0
    }
  ],
  "Timings": {
    "Build": 45.2,
    "InfrastructureStartup": 62.1,
    "Scenario1": 135.0,
    "Scenario4": 222.0
  }
}
```

### Error Handling

- **Continue on scenario failure**: When one scenario fails, log the failure and continue to the next scenario (don't abort the entire regression run)
- **Exit code**: Return non-zero if any scenario failed
- **Per-scenario logs**: Each scenario already gets its own log file under `results/logs/`

## Scope

### In Scope

- Adding `-Scenario All` support to `Run-IntegrationTests.ps1`
- Interactive menu option for full regression
- Lightweight JIM reset between scenarios
- Samba AD cleanup between scenarios
- Aggregated results JSON
- Combined performance summary

### Out of Scope

- Parallel scenario execution (scenarios share the same JIM instance)
- Changes to individual scenario scripts (they remain independent)
- New test scenarios
- CI/CD pipeline integration (future work)

## Implementation Notes

### Files to Modify

- `test/integration/Run-IntegrationTests.ps1`: Main changes:
  - Add `All` handling in scenario selection/validation
  - Add "All Scenarios" to interactive menu
  - Extract per-scenario execution into a function
  - Add lightweight reset function (JIM DB + Samba AD cleanup)
  - Add results aggregation logic
  - Move build, Samba image checks, and Docker cleanup outside the per-scenario loop

### Lightweight Reset Function

```powershell
function Reset-JIMForNextScenario {
    # 1. Stop JIM containers (keep external systems running)
    docker compose -f docker-compose.yml -f docker-compose.override.yml --profile with-db down -v 2>&1 | Out-Null

    # 2. Remove JIM database volume
    docker volume rm jim-db-volume 2>&1 | Out-Null

    # 3. Clean Samba AD test data
    docker exec samba-ad-primary samba-tool ou delete "OU=Corp,DC=panoply,DC=local" --force-subtree-delete 2>&1 | Out-Null
    # ... clean other OUs as needed

    # 4. Generate new API key, update .env

    # 5. Restart JIM containers
    docker compose -f docker-compose.yml -f docker-compose.override.yml --profile with-db up -d 2>&1 | Out-Null

    # 6. Wait for JIM API health check
}
```

### Template Handling

Scenarios that ignore the template parameter (2, 4, 6) will receive `Nano` as before. Scenarios that use template-based data (1, 5, 8) will use whatever template the user selected.
