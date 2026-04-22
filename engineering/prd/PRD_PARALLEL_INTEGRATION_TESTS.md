# Parallel All-Scenarios Integration Test Execution

- **Status:** Planned
- **Created:** 2026-04-22
- **Author:** Jay Van der Zant
- **Issue:** #636

## Problem Statement

The Pre-Release all-scenarios integration run is the gate we use before cutting a release. At the current hardcoded template sizes (Samba AD: MediumLarge, OpenLDAP: Scale100K) it takes a very long time to complete because every scenario goes through a full stand-up and tear-down of the JIM stack, directory servers, and data population — effectively running a dozen large-scale initialisations back-to-back on a single host.

This:

- Slows our ability to release often.
- Makes full regression after significant refactoring or bug-fix work prohibitively expensive.
- Prevents us from making good use of spare resources on the test host when running Pre-Release locally.
- Hardcodes the Pre-Release template sizes, so there is no way to dial the run down when iterating locally or up when stress-testing.

The cache infrastructure is already in place (content-hash-keyed CSV tars, Samba AD / OpenLDAP snapshot images tagged per template) but the runner does not currently exploit it for parallel fan-out, and the Docker Compose project uses fixed container / volume names that prevent multiple scenarios running side-by-side on the same host.

## Goals

- Pre-Release mode can run multiple scenarios in parallel on a single host, with the degree of parallelism controlled by an explicit option; default behaviour is unchanged (serial).
- Pre-Release mode can run with caller-chosen Samba AD and OpenLDAP template sizes via both CLI parameters and the interactive menu.
- A deliberate "prepare" phase runs once per invocation, warming all shared artifacts (JIM image, directory snapshot images, CSV caches) before any scenario shard starts, so parallel shards do no redundant setup work.
- A GitHub Actions workflow can run the Pre-Release suite as a matrix on a self-hosted runner pool, with access gated to authorised users on authorised triggers.
- Scenario results from parallel shards are merged into a single regression report with the same shape as today's `full-regression-<timestamp>.json` — downstream tooling and the release skill are unaffected.
- Wall-clock time for a Pre-Release run at MediumLarge/Scale100K is meaningfully shorter than today's serial baseline when run with `-Parallelism > 1` on a suitably-resourced host or across a matrix of runners.

## Non-Goals

- Parallelising scenarios *within* a single JIM instance. Each shard still runs one scenario end-to-end.
- Parallelising existing unit or API test projects (`test/JIM.*.Tests/`). This PRD is strictly about `test/integration/`.
- Reducing the work a single scenario performs (population, sync runs, sync-rule evaluation). Time savings come from fan-out and cache reuse, not scenario-level optimisation.
- Building a general-purpose test orchestrator. The runner stays a PowerShell script; fan-out is handled by `ForEach-Object -Parallel` locally and by GitHub Actions matrix jobs in CI.
- Introducing a new cloud dependency for result storage. Reports continue to land in `test/integration/results/`.
- Sharing a single Postgres or directory server between shards. Every shard gets a fully isolated stack.

## User Stories

1. As a release engineer, I want to run `Run-IntegrationTests.ps1 -Scenario Pre-Release -Parallelism 4` on my workstation, so that the full regression suite completes in a fraction of the time while using the resources available on the host.
2. As a developer iterating locally, I want to pick smaller Samba AD and OpenLDAP template sizes when running Pre-Release, so that I can get regression signal within my available time budget without being forced onto Scale100K.
3. As a release engineer cutting a scheduled release, I want GitHub Actions to run the Pre-Release suite on self-hosted runners as a matrix, so that release gates run on dedicated hardware without blocking my workstation.
4. As a maintainer, I want the self-hosted runner workflow to refuse to execute for untrusted contributors or untrusted triggers, so that our hardware is not exposed to malicious PR-driven code execution.
5. As any developer, I want serial Pre-Release runs to behave exactly as they do today when I do not opt into parallelism, so that I can trust the default path is unchanged.

## Requirements

### Functional Requirements

#### Parameter and menu surface

1. `Run-IntegrationTests.ps1` must accept a `-Parallelism` parameter, accepting integers >= 1 and the literal value `Max`. Default is `1` (serial).
2. When `-Scenario Pre-Release` is selected, the runner must honour `-TemplateSambaAD` and `-TemplateOpenLDAP` parameters. If either is omitted, the current defaults apply (`MediumLarge` for Samba AD, `Scale100K` for OpenLDAP).
3. The interactive menu's Pre-Release option must prompt, in order, for:
   1. Samba AD template size (full `ValidateSet`, default `MediumLarge`)
   2. OpenLDAP template size (full `ValidateSet`, default `Scale100K`)
   3. Parallelism (`1`, `2`, `4`, `8`, `Max`, or custom integer; default `1`)
   4. Existing options unchanged (log level, change tracking, etc.)
4. `Max` parallelism resolves to the number of scenarios that will run in the current invocation.
5. Invalid parameter combinations (e.g. `-Parallelism 0`, an unknown template name) must fail fast with a clear error before any setup begins.

#### Prepare-once phase

6. Before any scenarios execute, the runner must run an explicit prepare phase that ensures the following artifacts are present and up-to-date for the chosen template sizes:
   - JIM Docker image (via the existing build path).
   - Samba AD snapshot image(s) for the chosen `-TemplateSambaAD`.
   - OpenLDAP snapshot image(s) for the chosen `-TemplateOpenLDAP`.
   - CSV cache tars for the chosen template(s).
7. The prepare phase must reuse the existing content-hash cache keys unchanged — no new cache format.
8. If the prepare phase fails, no scenario shards are launched and the runner exits with a non-zero code.
9. The prepare phase must be idempotent: on a warm host where caches are present, it completes with minimal work (image/cache hash checks only).

#### Per-scenario isolation (Option B — always auto-named)

10. The integration Compose file must remove all explicit `container_name:` entries for services that vary per shard (Samba AD primary/source/target, OpenLDAP, JIM Web, JIM Worker, JIM Scheduler, JIM DB, CSVs helper). Compose auto-naming (`<project>-<service>-<replica>`) applies in all runs, serial and parallel.
11. The runner must set `COMPOSE_PROJECT_NAME` per invocation:
    - Serial runs: `jim-integration` (unchanged project name).
    - Parallel runs: `jim-integration-s<N>` where `<N>` is the 1-based shard index.
12. All scripts that currently reference containers or volumes by fixed name must be updated to look them up by Compose project + service name (e.g. `docker compose -p <project> ps -q <service>`), so the code path is identical regardless of project suffix.
13. Host port mappings for Samba AD and OpenLDAP that are only required for in-stack test calls must be removed; the runner executes those calls via `docker compose exec`. Ports genuinely required for host-level debugging (e.g. JIM Web) remain, with documentation that only one shard can bind them at a time and the others run without them.
14. Named volumes in the Compose file must not use explicit `name:` properties for per-shard volumes — Compose auto-prefixes with the project name. The previously shared `jim-connector-files-volume` becomes a per-shard volume.
15. Between scenarios within the same shard, the existing lightweight reset behaviour must be preserved (DB volume wipe, directory OU cleanup, API key regeneration).

#### Parallel execution (local)

16. When `-Parallelism > 1`, the runner must launch up to that many scenario shards concurrently using `ForEach-Object -Parallel`.
17. Each shard owns its own Compose project name, log file, and result file. Shards must not share mutable state on disk beyond the read-only cache directory and the Docker image store.
18. The runner must stream a concise per-shard status line to the console (scenario name, shard index, state: running/passed/failed/skipped) so progress is visible without interleaving verbose scenario logs. Full per-shard logs are written to disk.
19. If `-ContinueOnFailure` is not set, a shard failure causes the runner to stop launching new shards; already-running shards finish and their results are included in the final report.
20. Total wall-clock time, per-shard durations, and prepare-phase duration must be recorded in the aggregated result.

#### Reporting and progress model

The runner separates output into three distinct channels so that parallel runs remain readable. Verbose scenario output never reaches the main console; the console is reserved for short lifecycle events per shard.

21. Each shard's verbose output (scenario stdout, stderr, Docker output, assertion detail) must be redirected to a per-shard log file at `test/integration/results/run-<runId>/scenario-<N>.log`. Nothing from this channel appears on the main console during a parallel run.
22. Each shard emits short lifecycle *status events* at defined points: `Launched`, `SetupComplete`, `ScenarioRunning`, `ScenarioComplete` (with outcome and duration), and `TeardownComplete`. Each event is a structured record carrying shard index, scenario name, event type, timestamp, and optional payload (duration, exit code, error message).
23. Status events must be delivered to the parent runner via a shared `System.Collections.Concurrent.ConcurrentQueue[PSObject]` passed to each `ForEach-Object -Parallel` runspace via `$using:`. The parent drains the queue on a short interval (e.g. every 500 ms) and writes one prefixed line per event to the console, e.g. `[s2] Scenario 4 - setup complete (42s)`.
24. When a shard launches, the parent must print the shard's log file path to the console so developers can `tail -f` it on demand for detailed progress.
25. When a shard completes with a failure, the parent must print the outcome line plus a tail of the shard's log file (last 50 lines, configurable) to the console, so common failures can be diagnosed without opening the log file.
26. The end-of-run summary must print: total wall-clock duration, prepare-phase duration, per-shard passed/failed/skipped counts, paths to the per-shard logs, and the path to the merged `full-regression-<timestamp>.json`.
27. Serial runs (`-Parallelism 1`) must bypass the queue and status-event machinery entirely — console output falls back to today's inline behaviour. Only the result-file split (per-shard JSON written by the shard, merge step at the end) applies in serial mode, so the merged report shape is identical across serial and parallel runs.
28. If the parent runner is interrupted (Ctrl+C), it must write a `run-interrupted.json` marker, attempt to collect any pending status events from the queue, best-effort tear down in-flight shards, and run the merge step against whatever shard results already exist on disk.

#### Result sharding and merge

29. Each shard writes its result to `test/integration/results/run-<runId>/scenario-<N>.json` using the same per-scenario block shape produced today at [Run-IntegrationTests.ps1:1431-1439](../../test/integration/Run-IntegrationTests.ps1#L1431-L1439). Writing the result is the shard's responsibility — the parent only reads files, it never marshals result objects across runspaces.
30. After all shards complete (success or failure), a merge step reads the shard directory and writes `test/integration/results/full-regression-<timestamp>.json` in the same shape currently written at [Run-IntegrationTests.ps1:1517-1544](../../test/integration/Run-IntegrationTests.ps1#L1517-L1544) — same field names, same nesting, same schema.
31. The merge step must be invokable standalone (for the Actions matrix "merge" job) and also run automatically at the end of a local parallel run.
32. If the runner is interrupted (Ctrl+C) mid-run, the merge step must still produce a report for any shards that completed and mark the run as interrupted (see requirement 28).

#### GitHub Actions workflow

33. A new workflow must run the Pre-Release suite as a matrix on self-hosted runners tagged for integration testing.
34. The workflow must:
    - Be triggered only by `workflow_dispatch` or by `pull_request_target` that carries a maintainer-applied label (exact label name TBD during implementation; proposed `integration:pre-release`).
    - Require approval via a GitHub Environment with required reviewers before any job touches a self-hosted runner.
    - Run a single "prepare" job on a self-hosted runner, producing warm caches and images on the shared pool.
    - Fan out to a matrix of scenario jobs, each running one scenario shard.
    - Finish with a merge job that aggregates shard results into a single artifact in the same shape as the local runner.
35. The workflow file must be covered by CODEOWNERS so it cannot be modified without maintainer review.
36. Self-hosted runners must be registered in ephemeral mode (`--ephemeral`) so the runner process exits after a single job.
37. No repository-wide secrets may be exposed to integration test jobs; only job-scoped secrets strictly required for the run.

#### Security controls for self-hosted runners

38. The runner host environment must restrict outbound network egress to a documented allowlist (github.com, ghcr.io, package registries, internal artifact stores as needed). The exact allowlist is captured in a runbook in `engineering/` at implementation time.
39. The self-hosted runner pool must not be used by any other workflow in the repository. Enforcement is via a runner label scoped to the Pre-Release workflow only.
40. The implementation phase must produce an `engineering/SELF_HOSTED_RUNNER_SECURITY.md` runbook documenting: trigger gating, approval flow, runner provisioning, ephemeral lifecycle, egress allowlist, secret handling, and the incident response procedure if a compromise is suspected.

### Non-Functional Requirements

- Serial (`-Parallelism 1`) runs must not regress in wall-clock time by more than 5% compared to today's baseline for the same template sizes.
- The prepare phase must be observable: its duration and cache hit/miss decisions for each artifact are logged.
- Per-shard logs must be retrievable after a run (on disk for local runs, as workflow artifacts for CI).
- The runner must not leave orphaned Compose projects, containers, or volumes after a successful run. On failure, cleanup is best-effort but the runner must print the exact `docker compose -p <project> down -v` commands needed to clean up manually.
- British English throughout, per CLAUDE.md.

## Examples and Scenarios

### Scenario 1: Developer local Pre-Release at reduced scale

**Given** a developer wants a quick regression pass before lunch
**When** they run `./Run-IntegrationTests.ps1 -Scenario Pre-Release -TemplateSambaAD Small -TemplateOpenLDAP Medium -Parallelism 4`
**Then** the prepare phase warms CSVs and snapshot images for `Small` / `Medium` only, four scenario shards run concurrently with Compose projects `jim-integration-s1` through `jim-integration-s4`, and a single aggregated report lands in `test/integration/results/full-regression-<timestamp>.json` in the same shape as today.

### Scenario 2: Serial Pre-Release (default, unchanged behaviour)

**Given** a developer runs `./Run-IntegrationTests.ps1 -Scenario Pre-Release`
**When** no parallelism, template, or other new flags are passed
**Then** the runner executes scenarios one at a time under the `jim-integration` Compose project, with Samba AD at MediumLarge and OpenLDAP at Scale100K — the only visible difference from today is that container names carry the Compose project prefix (e.g. `jim-integration-samba-ad-primary-1` instead of `samba-ad-primary`).

### Scenario 3: Interactive menu selection

**Given** a developer launches `./Run-IntegrationTests.ps1` with no parameters
**When** they select the "Pre-Release" menu option
**Then** the menu prompts for Samba AD template size (default MediumLarge), OpenLDAP template size (default Scale100K), parallelism (default 1), and existing options, then runs with their selections.

### Scenario 4: GitHub Actions release gate

**Given** a maintainer applies the `integration:pre-release` label to a release PR
**When** the Pre-Release workflow is triggered and an authorised reviewer approves the environment
**Then** one prepare job warms the self-hosted pool, N matrix jobs each run one scenario on an ephemeral runner, and a merge job produces a single regression artifact attached to the workflow run. An untrusted PR author cannot cause any of this to run because they cannot apply the label.

### Scenario 5: Container naming under parallelism

**Given** `-Parallelism 2` is active
**When** the runner inspects Docker state mid-run
**Then** `docker ps` shows containers named e.g. `jim-integration-s1-samba-ad-primary-1`, `jim-integration-s1-jim-openldap-1`, `jim-integration-s2-samba-ad-primary-1`, `jim-integration-s2-jim-openldap-1` — each shard's role is obvious from the name.

### Scenario 6: Shard failure handling without `-ContinueOnFailure`

**Given** four shards are running and shard 2 fails mid-scenario
**When** the failure is detected
**Then** no further shards are launched, the three shards that had already started complete their current scenario, each writes its result JSON, and the final report records the run as failed with shard 2's exit code and error surfaced clearly.

### Scenario 7: Console output during a parallel run

**Given** a developer runs `./Run-IntegrationTests.ps1 -Scenario Pre-Release -Parallelism 4`
**When** scenarios are executing
**Then** the console shows short, readable status lines prefixed with the shard index, with no interleaved verbose output — for example:

```
Launching 4 shards, logs at test/integration/results/run-20260422-143012/
[s1] Scenario 1 -> scenario-1.log
[s2] Scenario 4 -> scenario-2.log
[s3] Scenario 5 -> scenario-3.log
[s4] Scenario 6 -> scenario-4.log
[s1] setup complete (38s)
[s2] setup complete (41s)
[s3] setup complete (44s)
[s4] setup complete (45s)
[s1] scenario running
[s2] scenario running
...
[s1] PASSED in 5m 18s
[s3] FAILED (exit 1) in 6m 02s - last 50 lines of scenario-3.log:
    <...tail dumped here...>
[s2] PASSED in 5m 47s
[s4] PASSED in 6m 11s

Run complete: 3 passed, 1 failed, 0 skipped, total 6m 14s
Merged report: test/integration/results/full-regression-20260422-143626.json
```

Developers wanting detailed progress on any specific shard `tail -f` its log file using the path printed at launch.

## Constraints

- Must remain cross-platform PowerShell — no bash scripts, per CLAUDE.md.
- Must work in air-gapped environments: no new cloud dependencies for result storage or orchestration.
- Must not introduce new NuGet packages; no .NET code changes are expected (this is purely test harness).
- Must preserve the existing `full-regression-<timestamp>.json` shape so the `/release` skill and any downstream tooling continue to work unchanged.
- Must not expand the supported template size set; template names remain the existing `ValidateSet`.
- Self-hosted runner workflow must follow the security controls listed under Functional Requirements; this is a hard gate.

## Affected Areas

| Area | Impact |
|------|--------|
| Runner script | `Run-IntegrationTests.ps1`: new `-Parallelism` parameter, new Pre-Release menu prompts, prepare phase extracted, parallel fan-out loop, shard result writing |
| Runner helper | New `Merge-RegressionResults.ps1` script; all scripts referencing containers by fixed name updated to go via Compose project + service lookup |
| Docker Compose | `test/integration/docker/docker-compose.integration-tests.yml`: remove `container_name:` entries, remove explicit `name:` on per-shard volumes, review host port mappings |
| CI/CD | New workflow `.github/workflows/integration-pre-release.yml` for self-hosted matrix runs, plus CODEOWNERS entry |
| Documentation | `test/integration/README.md` updated with parallelism guidance; new `engineering/SELF_HOSTED_RUNNER_SECURITY.md` runbook |
| Release skill | No change required — relies on `full-regression-<timestamp>.json` shape which is preserved |

## Dependencies

- Design, provisioning, and hardening of a self-hosted GitHub Actions runner with sufficient compute/memory/IO to host the Pre-Release matrix at MediumLarge / Scale100K templates. This work is tracked internally and is not detailed here. Phase 4 (GitHub Actions matrix) is blocked on it; Phases 1-3 (local parallelism) are independent and can land first.
- Agreement on the exact PR label name that gates the self-hosted workflow.

## Open Questions

1. Should `-Parallelism Max` cap at some sensible upper bound (e.g. scenario count, or CPU-count / 2) to avoid thrashing a small host, or trust the caller's input verbatim?
2. For the CI matrix, do we want one scenario per runner (simplest, maximum wall-clock win) or N scenarios per runner with local parallelism inside (fewer runners required)? Recommend starting with one-scenario-per-runner and revisiting if the pool gets expensive.
3. Do we need a way to run the Pre-Release workflow against a specific ref other than the PR head (e.g. re-run against `main` after a merge) in the same workflow, or is a separate `workflow_dispatch` path sufficient?
4. Should host port mappings (currently used for some debugging) be emitted for shard 1 only when running parallel locally, so a developer can still browse the JIM UI in the first shard, or removed entirely for parallel runs?

## Acceptance Criteria

### Phase 1 — Parameters, menu, prepare phase, result sharding (no parallelism yet)

- [ ] `-Parallelism` parameter exists and is validated; default is 1 (serial).
- [ ] Pre-Release honours `-TemplateSambaAD` / `-TemplateOpenLDAP` when supplied; hardcoded defaults apply otherwise.
- [ ] Interactive Pre-Release menu prompts for Samba AD template, OpenLDAP template, and parallelism in addition to existing options.
- [ ] Prepare phase is an explicit step in the runner with its own log output and timing; cache hits/misses are visible.
- [ ] Each scenario writes its result to `test/integration/results/run-<runId>/scenario-<N>.json`; the merge step produces `full-regression-<timestamp>.json` in the same shape as today.
- [ ] Serial run wall-clock time is within 5% of the pre-change baseline.

### Phase 2 — De-hardcode Compose naming (Option B)

- [ ] `container_name:` entries removed from `docker-compose.integration-tests.yml` for every per-shard service.
- [ ] All runner scripts that look up containers or volumes do so via Compose project + service, not fixed names.
- [ ] Serial runs pass a full Pre-Release regression with new auto-naming.
- [ ] `README.md` in `test/integration/` documents the new container-name pattern.

### Phase 3 — Local parallelism

- [ ] `-Parallelism N` (N > 1) launches up to N shards concurrently under distinct `COMPOSE_PROJECT_NAME` values.
- [ ] Per-shard logs and results land in distinct paths; no cross-shard collisions in Docker state or on disk.
- [ ] A Pre-Release run at MediumLarge / Scale100K with `-Parallelism 4` on a suitably-resourced host completes in meaningfully less wall-clock time than serial (target: at least 2x speedup; exact ratio depends on host).
- [ ] Console output during parallel runs is limited to prefixed status events; verbose scenario output is redirected to per-shard log files whose paths are printed at shard launch.
- [ ] Failed shards print the tail of their log file (default 50 lines) to the console on completion.
- [ ] Shard failure handling works with and without `-ContinueOnFailure`.
- [ ] Interrupted runs still produce a merged report for completed shards and leave a `run-interrupted.json` marker.

### Phase 4 — GitHub Actions self-hosted matrix

- [ ] Workflow `integration-pre-release.yml` exists and runs only on authorised triggers (`workflow_dispatch` or labelled `pull_request_target`).
- [ ] Workflow requires Environment approval before any self-hosted job runs.
- [ ] Workflow file is covered by CODEOWNERS.
- [ ] Self-hosted runners run in ephemeral mode and are scoped to this workflow via label.
- [ ] Prepare / matrix / merge job topology produces a single regression artifact in the expected shape.
- [ ] `engineering/SELF_HOSTED_RUNNER_SECURITY.md` runbook exists and covers the controls listed in Non-Functional Requirements.

## Additional Context

- Existing cache infrastructure: `test/integration/Get-OrGenerate-TestCSV.ps1`, `test/integration/Generate-TestCSV.ps1`, `test/integration/Build-SambaSnapshots.ps1`, `test/integration/Build-OpenLDAPSnapshots.ps1`, `test/integration/Test-CsvCache.ps1`.
- Current Pre-Release hardcodes: [Run-IntegrationTests.ps1:1004-1005](../../test/integration/Run-IntegrationTests.ps1#L1004-L1005).
- Current per-scenario result capture: [Run-IntegrationTests.ps1:1431-1439](../../test/integration/Run-IntegrationTests.ps1#L1431-L1439).
- Current aggregated report shape: [Run-IntegrationTests.ps1:1517-1544](../../test/integration/Run-IntegrationTests.ps1#L1517-L1544).
- GitHub issue: [#636](https://github.com/TetronIO/JIM/issues/636).
- Related: supply chain / security hardening discussion lives alongside the existing Trivy / CVE policies in `engineering/DEVELOPER_GUIDE.md`.
