# Parallel Directory Lanes for Pre-Release Integration Tests

- **Status:** Doing
- **Created:** 2026-04-22
- **Revised:** 2026-09-29 (rescoped from per-scenario sharding plus a self-hosted CI matrix to three parallel directory lanes on one host)
- **Author:** Jay
- **Issue:** #636

## Problem Statement

Pre-Release is the regression gate before a release, and it now takes about 4 hours 40 minutes. It runs three directory passes back to back, and each pass runs the full scenario list against one directory:

| Pass | Template | Duration (2026-09-29 run) |
|------|----------|---------------------------|
| Samba AD | Medium | 1h 24m |
| OpenLDAP | Large | 1h 38m |
| 389 Directory Server | Large | 1h 38m |

The passes do not depend on each other, and the host has spare capacity: the dev host has 16 cores and 121 GB of memory, and a single pass uses a fraction of both. The passes still run one at a time because the harness assumes it owns the whole Docker host. It uses fixed container names, fixed host ports, fixed volume names, a shared `.env` file and fixed result and test-data paths, and several of its reset and clean-up steps act host-wide. The harness says so itself: `Clear-StaleIntegrationMonitors` justifies its host-wide sweep with "concurrent runner invocations are impossible on one host".

The long run slows releases, makes a full regression after a significant fix expensive enough to skip, and ties up the host for most of a working day.

The original version of this PRD proposed sharding by scenario (N stacks, one scenario each) and a self-hosted GitHub Actions matrix. That design needs far more isolation work, a merge of per-scenario results, and runner infrastructure that does not exist yet. Running the three directory passes side by side gets most of the wall-clock saving for a fraction of that work, because the unit of parallelism already exists: the pass.

## Goals

- Pre-Release can run its three directory passes at the same time on one host, each in its own isolated copy of the stack (a "lane").
- A parallel Pre-Release run finishes in roughly the time of its slowest lane, targeting no more than 2 hours against today's 4 hours 40 minutes on the dev host.
- Serial Pre-Release and every single-directory run behave exactly as today, and stay the default.
- A failure in one lane cannot corrupt, reset or delete another lane's containers, volumes, data or results.
- The run produces one combined summary covering all three lanes, as well as each lane's own `full-regression-*.json`.

## Non-Goals

- **Sharding by scenario, or more than three lanes.** One lane per directory type only. Scenario-level fan-out stays a possible follow-up if lanes prove the isolation model.
- **A GitHub Actions matrix on self-hosted runners.** Split out of this PRD; it depends on runner provisioning and security work that is not started. It should get its own issue.
- **Running lanes across several hosts.** One host only.
- **Changing what any scenario does or asserts.** No scenario logic changes beyond swapping fixed names, URLs and paths for lane-scoped ones.
- **Sharing a JIM stack, Postgres or directory between lanes.** Every lane gets its own.
- **Removing the duplicate runs of directory-agnostic scenarios.** Already done in #1851; this PRD builds on it.

## User Stories

1. As a release engineer, I want `Run-IntegrationTests.ps1 -PreRelease -Parallel` to run all three directory passes at once, so that the release gate takes under 2 hours instead of most of a day.
2. As a developer after a significant fix, I want a full regression that finishes within an afternoon, so that I actually run it rather than skipping it.
3. As any developer, I want a run without `-Parallel` to behave exactly as it does today, so that I can trust the default path.
4. As a developer reading a failed parallel run, I want to see which lane failed, the tail of its log, and where its full log lives, without the three lanes' output interleaved on my console.

## Requirements

### Functional Requirements

#### Parameters and menu

1. `Run-IntegrationTests.ps1` accepts a `-Parallel` switch. It is valid only with `-DirectoryType All` (including `-PreRelease`), and fails fast with a clear message otherwise.
2. Without `-Parallel`, behaviour is unchanged: the three passes run one after another, in the current order, with the current console output.
3. The interactive menu's Pre-Release entry asks whether to run the lanes in parallel (default: no), after the existing prompts.
4. `-TemplateSambaAD`, `-TemplateOpenLDAP` and `-TemplateDirectoryServer389` keep working as today, per lane.

#### Lane isolation

5. A lane is one directory pass (Samba AD, OpenLDAP or 389 Directory Server) with its own stack. No two lanes may share a container, volume, network, host port, or any file written during the run.
6. Each lane runs in its own `pwsh` process, not a runspace. Several settings the harness relies on (`SAMBA_IMAGE_*`, `OPENLDAP_*`, `DIRSRV_*`, `JIM_DB_*`, `VERSION_SUFFIX`, `JIM_RUNPROFILE_ABORT_SENTINEL`) are process environment variables, which runspaces would share.
7. Each lane has its own Docker Compose project name for both the JIM stack and the integration compose file. A serial run keeps today's project names (`jim` and `jim-integration`), so serial behaviour and container names are unchanged.
8. Containers are reached by lane-scoped names wherever scripts use a fixed name today (`jim.web`, `jim.worker`, `jim.scheduler`, `jim.database`, the directory containers via `Get-DirectoryConfig`, and the fixed `docker rm -f` list). Scripts resolve the name for the current lane rather than hardcoding it.
9. Each lane publishes JIM on its own host port, and every script that calls `http://localhost:5200` today uses the lane's URL instead. The scripts run on the host (the PowerShell module talks HTTP to JIM), so a host port is required; reaching JIM over the Docker network is not an option.

   | Lane | JIM (web and API) | Postgres | Keycloak |
   |------|-------------------|----------|----------|
   | Samba AD | 5200 | 5432 | 8180, plus the 8181 bridge |
   | OpenLDAP | 5300 | 5433 | not published |
   | 389 Directory Server | 5400 | 5434 | not published |

   The Samba AD lane keeps today's ports, so a serial run and the first lane look identical. 5201 and 5210 are already taken by the devcontainer (JIM.Web over HTTPS, and `dotnet watch` browser refresh), hence the jump to 5300 and 5400. Postgres gets a port per lane so each lane's database can still be inspected with local tools.
10. Each lane runs its own Keycloak, as part of its own JIM stack. No scenario uses SSO (they all authenticate with the infrastructure API key), but JIM.Web fetches the OIDC discovery document at startup and will not start without it, and `jim.web` waits for Keycloak to report healthy. A per-lane Keycloak keeps today's stack definition unchanged and adds no coupling between lanes. It costs up to 2.5 GB of memory per lane, which the host can afford.
11. Only the Samba AD lane's web front end can be signed in to from a browser. The other lanes do not publish Keycloak, so their front ends are API-only during a parallel run. This is deliberate: pointing two JIM stacks at one browser-facing Keycloak address is exactly what produces `invalid_grant: Code not valid` sign-in failures. To debug another lane's front end, re-run that directory type on its own.
12. Each lane has its own named volumes (JIM database, logs, keys, connector files, and the `jim-integration-*` set) and its own Docker network.
13. Each lane has its own settings file in place of the shared `.env` rewrite, and its own infrastructure API key. No lane edits the repository's `.env` during a parallel run.
14. Each lane writes its test data (HR CSVs and scenario-specific files currently written to fixed names under `test/test-data/` and fixed `/tmp` paths) to a lane-scoped location. The shared CSV cache stays shared and read-mostly, with cache writes made atomic (unique temp file, then rename) so two lanes cannot corrupt one entry.

#### Scoped reset and clean-up

15. Every reset step acts only on its own lane: `down -v`, volume removal, connector-volume clearing, the between-scenario reset, and directory OU clean-up. Removing volumes "matching `jim-integration`" or by a fixed global name is not allowed in a parallel run.
16. The stale-monitor sweep (`Clear-StaleIntegrationMonitors`) reaps only the current lane's monitors and sidecars.
17. Host-wide clean-up (the Step 7 image and build-cache prune) never runs inside a lane. In a parallel run it runs once, in the parent, after every lane has finished.
18. The error watcher and the post-scenario error scan read only the current lane's JIM containers, and each lane's error sentinel file is its own.
19. Docker stats and Docker events capture are filtered to the current lane's containers.

#### Prepare once, then fan out

20. Before any lane starts, the parent runs a prepare phase that builds the JIM images once and makes sure every snapshot image and CSV cache entry the three lanes need exists. Lanes then start without building anything, so no two processes build or tag the same image at once.
21. If the prepare phase fails, no lane starts and the run exits non-zero.

#### Directory-agnostic scenarios and shared databases

22. Directory-agnostic scenarios (011, 015, 016) run in exactly one lane, as today. In a parallel run they go to the lane expected to finish first (Samba AD at the current templates), to keep the lanes balanced.
23. Scenario 016's database containers (`sqlserver-hris-a`, `oracle-hris-b`) stay shared, outside every lane, and preserved across runs. Today they join the fixed `jim-network`; in a parallel run the lane that runs Scenario 016 connects them to its own network (`docker network connect`) before the scenario starts and disconnects them afterwards. No other lane starts, stops, reseeds or connects to them.
24. Each lane's JIM Postgres keeps today's sizing rule: the runner picks the profile from that lane's template and passes it through `JIM_DB_*` process environment variables, so separate lane processes (requirement 6) size their own databases with no extra work. At Large the dev profile (256 MB shared buffers) peaked at 0.53 GB, so it needs no change for three lanes.
25. `-FullMatrix` is allowed with `-Parallel`. It moves only the JIM database of the lane that runs Scenario 016 to the Scale500k profile (4 GB shared buffers), because that setting is also a per-process environment variable. The other lanes are unaffected.

#### Running, reporting and failure handling

26. The parent prints one short, prefixed status line per lane event (started, each scenario passed or failed with duration, lane finished), for example `[OpenLDAP] Scenario-008 passed (7m 32s)`. Each lane's verbose output goes to its own log file, whose path the parent prints when the lane starts.
27. When a lane fails, the parent prints the failure and the last 50 lines of that lane's log. The other lanes run to completion, because their results remain valid and are the point of the run.
28. Every result and log file a lane writes carries the lane's directory type in its name, so two lanes finishing in the same second cannot overwrite each other.
29. After all lanes finish, the parent writes a combined summary (JSON plus console table) of each lane's outcome, duration and per-scenario results. Each lane still writes its own `full-regression-*.json` in today's shape, so existing tooling keeps working.
30. On Ctrl+C the parent stops all lanes, and each lane cleans up only its own containers and volumes. If clean-up cannot finish, the parent prints the exact commands needed to finish it by hand.
31. The run exits non-zero if any lane failed.

### Non-Functional Requirements

- A parallel Pre-Release on the dev host finishes within 15% of its slowest lane's serial duration.
- A serial Pre-Release does not get slower by more than 5%.
- Before starting a parallel run, the parent checks available memory and cores and warns if the host looks too small. Measured on the 2026-09-29 run at Large, one lane (JIM stack including Keycloak, plus its directory) peaks at about 5 GB of memory and 4 cores, and Scenario 016's shared databases add about 3.5 GB. The warning thresholds are therefore 20 GB available memory and 12 cores. The dev host (121 GB, 16 cores) clears both comfortably.
- A successful parallel run leaves no orphaned lane containers, volumes or networks.
- British English throughout.

## Examples and Scenarios

### Scenario 1: Parallel Pre-Release

**Given** a warm dev host
**When** a developer runs `./test/integration/Run-IntegrationTests.ps1 -PreRelease -Parallel`
**Then** the prepare phase builds the JIM images and checks the snapshot images once, three lanes start, and the console shows short prefixed lines:

```
Prepare phase complete (3m 12s)
[SambaAD]   started -> results/logs/lane-SambaAD-2026-09-29_101500.log
[OpenLDAP]  started -> results/logs/lane-OpenLDAP-2026-09-29_101500.log
[389DS]     started -> results/logs/lane-DirectoryServer389-2026-09-29_101500.log
[SambaAD]   Scenario-001 passed (9m 40s)
[OpenLDAP]  Scenario-001 passed (12m 09s)
...
[OpenLDAP]  Scenario-023 FAILED (11m 47s) - last 50 lines:
    ...
[389DS]     finished: 17 passed, 0 failed (1h 41m)

Pre-Release: 2 of 3 lanes passed, 1h 44m total
Summary: results/pre-release-2026-09-29_101500.json
```

### Scenario 2: Serial Pre-Release is unchanged

**Given** a developer runs `./test/integration/Run-IntegrationTests.ps1 -PreRelease`
**When** `-Parallel` is not passed
**Then** the three passes run one after another, with today's container names, ports, `.env` handling and console output.

### Scenario 3: One lane's reset leaves the others alone

**Given** a parallel run in which the OpenLDAP lane has just finished a scenario
**When** that lane runs its between-scenario reset (database volume removal, connector volume clear, `down -v`)
**Then** only the OpenLDAP lane's containers and volumes are touched, and the Samba AD and 389 Directory Server lanes carry on without error.

### Scenario 4: Invalid combination

**Given** a developer runs `-Scenario Scenario-008-CrossDomainEntitlementSync -DirectoryType OpenLDAP -Parallel`
**When** the runner validates its parameters
**Then** it exits before any set-up with: `-Parallel runs the three directory passes side by side, so it needs -DirectoryType All (or -PreRelease).`

### Scenario 5: Ctrl+C mid-run

**Given** a parallel run with all three lanes mid-scenario
**When** the developer presses Ctrl+C
**Then** all three lanes stop, each removes its own containers, volumes and network, the parent writes the summary for what finished, and Scenario 016's database containers are left in place.

## Constraints

- PowerShell only; no bash scripts for the harness (per CLAUDE.md).
- Air-gap friendly: no new cloud dependencies.
- No new NuGet packages and no product code changes; this is test harness work only.
- Each lane's `full-regression-*.json` keeps today's shape.
- Template names stay the existing `ValidateSet`.
- The harness keeps building from the working tree, and keeps refusing git worktrees unless `-AllowWorktree` is passed. All three lanes run from the same checkout.
- Must also work on the devcontainer's inner Docker engine; never on the Windows host's Docker Desktop (per CLAUDE.md).

## Affected Areas

| Area | Impact |
|------|--------|
| Runner | `Run-IntegrationTests.ps1`: `-Parallel` switch and menu prompt, prepare phase, lane launch as child processes, prefixed status output, combined summary, lane-scoped reset, parent-only image prune |
| Helpers | `utils/Test-Helpers.ps1`: lane-aware container lookup, JIM URL, error watcher, stats/events capture, stale-monitor sweep, connector-volume clearing, CSV cache atomic writes |
| Scenarios | Swap fixed container names, `http://localhost:5200` defaults and fixed test-data paths for lane-scoped ones; no logic changes |
| Compose | Root `docker-compose.yml` / override and `test/integration/docker/docker-compose.integration-tests.yml`: project-name-scoped container names, volumes and network, and per-lane host ports for JIM, Postgres and Keycloak (requirement 9), with serial defaults unchanged |
| Snapshot builders | `Build-SambaSnapshots.ps1`, `Build-OpenLDAPSnapshots.ps1`, `Build-DirsrvSnapshots.ps1`: called from the prepare phase only in a parallel run |
| Scenario 016 | Shared database containers reachable from the lane that runs 016 |

## Documentation Impact

| Doc | Change |
|------|--------|
| `test/CLAUDE.md` | `-Parallel` usage, lane naming, and how to read and clean up a parallel run |
| `engineering/INTEGRATION_TESTING.md` | Lane model, isolation rules, and the per-lane resource estimate |
| `.devcontainer/CLAUDE.md` | How to remove a lane's containers, volumes and network left behind by an interrupted parallel run |

## Dependencies

- #1851 (directory-agnostic scenarios run once per Pre-Release), merged.

## Acceptance Criteria

- [ ] `-Parallel` exists, is refused without `-DirectoryType All`, and is offered in the Pre-Release menu entry.
- [ ] A serial Pre-Release passes with unchanged container names, ports and console output, and is no more than 5% slower.
- [ ] A parallel Pre-Release on the dev host passes all three lanes and finishes within 15% of the slowest lane's serial duration (target: under 2 hours).
- [ ] During a parallel run, `docker ps` shows three separate sets of JIM and directory containers, and no lane's reset, prune or monitor sweep touches another lane's containers, volumes or files.
- [ ] A deliberately failed scenario in one lane leaves the other two lanes to finish and pass, and the parent prints the failing lane's log tail.
- [ ] Ctrl+C stops all lanes and leaves no lane containers, volumes or networks behind, while Scenario 016's database containers remain.
- [ ] Every result and log file name carries its lane's directory type; the combined summary lists all three lanes.
- [ ] Docs in the table above are updated.

## Additional Context

- Pre-Release defaults and the `-DirectoryType All` loop: `test/integration/Run-IntegrationTests.ps1` (`-PreRelease` block, and the loop that re-invokes the script once per directory type).
- Host-wide operations to scope: the Step 1 reset, `Reset-JIMForNextScenario`, `Invoke-ImagePrunePreservingSnapshots` (Step 7) and `Clear-StaleIntegrationMonitors`.
- Directory-agnostic scenario handling: `test/integration/utils/Get-ScenarioDirectoryTypes.ps1` (#1851).
- GitHub issue: [#636](https://github.com/TetronIO/JIM/issues/636).
