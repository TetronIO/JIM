# PostgreSQL Tuning Measured Against JIM's Own Workloads

- **Status:** Doing (Phase 0 complete: results on #2834)
- **Issue:** [#2834](https://github.com/TetronIO/JIM/issues/2834)
- **Related:**
  - [#2669](https://github.com/TetronIO/JIM/issues/2669), the leaver-step measurements whose span breakdowns show where synchronisation time goes.
  - [#1943](https://github.com/TetronIO/JIM/issues/1943), which introduced the installer's sizing of the bundled PostgreSQL to the host.
- **Last Updated:** 2026-11-08

## Overview

JIM's PostgreSQL settings follow the pgtune formula: a quarter of the host's memory for `shared_buffers` (at most 8 GB), half for `effective_cache_size`, and a `work_mem` divided across every possible connection. Nobody has measured whether JIM's workloads want anything else. On scale runs, PostgreSQL never comes close to using the host's cores or memory, which raises two possibilities:
- it is held back by its settings;
- it is idle much of the time, waiting for JIM.

This plan finds out which, and then pushes every setting that matters well beyond the formula: larger, smaller, and switched off. A setting that crashes the database, or makes a run slower, is a result too. The aim is to know what JIM's workloads require of PostgreSQL, and to ship settings based on that.

## Why PostgreSQL may look idle when it is not held back

PostgreSQL does not work as SQL Server does, so its resource use reads differently:

- **One connection, one process, one core.** A query runs on a single core unless it is a large scan or aggregation that PostgreSQL decides to split across parallel workers. Inserts, updates and `COPY` (most of JIM's writing) never run in parallel. PostgreSQL can only use more cores when JIM sends more statements at once, and the Worker sends most of its work one statement at a time per page.
- **It leaves most of the memory to the operating system.** SQL Server takes memory for its own buffer pool. PostgreSQL keeps a smaller cache of its own and reads through the operating system's file cache, so memory that looks unused on the host is often caching the database.
- **Low CPU usually means waiting.** Either on the disk (flushing the write-ahead log at commit, or a checkpoint), or on the client, JIM, to send the next statement. The #2669 breakdowns already show long stretches of Worker-side time with no query running.

So the first question is not "which setting", but "for what share of each step is the database the thing being waited on".

## What we run today

There are three configurations, and they differ:

| Setting | Customer install (`docker-compose.yml`, sized by `setup.sh`) | Integration runs (`docker-compose.override.yml`, Scale500k profile) | Integration runs, Scale1m profile |
|---|---|---|---|
| `shared_buffers` | a quarter of host memory, at most 8 GB | 4 GB | 8 GB |
| `effective_cache_size` | half of host memory | 8 GB | 16 GB |
| `work_mem` | (host memory less `shared_buffers`) / 600, at least 4 MB | 64 MB | 64 MB |
| `maintenance_work_mem` | a sixteenth of host memory, at most 2 GB | 1 GB | 1 GB |
| `max_wal_size` / `min_wal_size` | 4 GB / 1 GB | 16 GB / 1 GB | 24 GB / 2 GB |
| `checkpoint_timeout` | 5 min (default) | 15 min | 15 min |
| `wal_buffers` | 16 MB | 16 MB | 16 MB |
| `wal_compression` | off (default) | lz4 | lz4 |
| `max_parallel_workers_per_gather` | 4 | 2 (default) | 2 (default) |
| `max_parallel_workers` / `max_worker_processes` | 16 / 16 | 8 / 8 (defaults) | 8 / 8 (defaults) |
| `jit` | on (default) | on (default) | on (default) |
| `synchronous_commit` | on (default) | on (default) | on (default) |

**The integration override leaves out the customer file's parallel-query settings**, so every scale measurement so far ran with PostgreSQL's defaults, not with what customers get. Phase 1 settles which to treat as the baseline.

## The host, and how far to trust it

The measurements run on the development host: about 62 GB of memory, and a Docker data drive (a QLC NVMe drive) with known write-stall waves under sustained heavy writes. Settings that change how much is written, or when, may look more decisive here than on a customer's storage. Every result records the host's disk wait alongside it, and a result that hinges on the disk is marked as such.

## Phase 0: where the time goes ✅

One Scale500k25kGroups run of Scenario 8, with no setting changed, instrumented to answer, for each Run Profile step:
- for what share of the step's wall time PostgreSQL had at least one active query from JIM;
- what those queries were waiting on;
- which statements took the time.

**Sampling, once a second, written to TSV beside the run's other results:**
- `pg_stat_activity` for JIM's connections: `state`, `wait_event_type`, `wait_event`, and the first 80 characters of the query. "Active with no wait event" is CPU. `IO:DataFileRead` is reading tables. `IO:WALSync` and `IO:WALWrite` are commits waiting on the disk. `Lock` and `LWLock` are contention. `Client:ClientRead` on an open transaction is PostgreSQL waiting for JIM.
- Host CPU (user, system, iowait) and disk throughput and wait, from `/proc` (`vmstat` and `iostat` style figures).
- Per-container CPU and memory, already sampled by the runner's `docker stats` job.

**At each step boundary:** a snapshot of `pg_stat_statements` (enabled with `CREATE EXTENSION`; the override already preloads it), `pg_stat_io`, `pg_stat_wal` and the checkpointer's statistics, so each step's statements, I/O and checkpoints can be told apart.

**Deliverable:** a table per step giving its wall time, database-active share, the wait breakdown and the top statements, posted on the issue. It sets the ceiling for every later phase: a step where the database is active for 30% of the time can gain at most 30% from database settings. It also says which settings are worth sweeping. For example, if commits waiting on the WAL dominate, the WAL settings matter; if `ClientRead` dominates, none of them do, and the time is JIM's to win.

The sampler is a PowerShell script under `test/integration/utils/`, started and stopped by the runner like the `docker stats` job, so later phases and later runs reuse it.

## Phase 1: a repeatable, cheaper benchmark

A full Scale500k25kGroups run takes about 65 minutes, mostly in the initial load, and its step times vary from run to run by a second or two. Twenty settings measured that way would cost a day of machine time, and still blur small effects.

1. **Snapshot after the initial load.** Stop the stack once the 500k initial synchronisation has finished, and copy the database volume and the OpenLDAP data volume. Each experiment restores both and replays the same later steps (joiner, mover and leaver cohorts, plus a Full Synchronisation), about 20 to 30 minutes.
2. **A way to pass any PostgreSQL setting to a run.** The override appends `${JIM_DB_EXTRA_ARGS}` to the database's command line, and the runner sets it from a new parameter (`-DatabaseSetting @{ jit = 'off'; work_mem = '256MB' }`). Settings on the command line override `ALTER SYSTEM`, so this is the only reliable way in.
3. **Settle the baseline.** Replay three times with the integration override's settings, and three times with the customer file's parallel-query settings added. This gives the run-to-run spread every later result is judged against, and decides which configuration to use as the baseline.

## Phase 2: one setting at a time, well beyond the formula

Each row is replayed at least twice, more where the effect is inside the baseline's spread. Phase 0's findings decide the order and may drop rows.

| Setting | Values to try | What it should show |
|---|---|---|
| `shared_buffers` | 2, 4, 8, 16, 32 GB | Whether a larger own cache beats leaving memory to the file cache |
| `work_mem` | 16 MB, 64 MB, 256 MB, 1 GB | Large sorts and hashes, such as the group-page join (6 s for one page in #2669) |
| `max_parallel_workers_per_gather` (with `max_parallel_workers` and `max_worker_processes` raised to match) | 0, 2, 4, 8, 16 | Whether the big reads split across cores, and whether that helps or hurts |
| `jit` | on, off | JIT compilation can cost more than it saves on short queries |
| `max_wal_size` and `checkpoint_timeout` | 4, 16, 64 GB; 5, 15, 30 min | Checkpoint pressure during bulk writes |
| `wal_buffers`, `wal_compression` | 16 MB, 64 MB, 256 MB; off, lz4 | Write-ahead log throughput |
| `synchronous_commit` | on, off | **Measurement only.** Shows how much of each step is spent waiting for commits to reach the disk. Never a default: it can lose committed work in a crash, which synchronisation integrity rules out |
| `effective_io_concurrency`, `random_page_cost` | 1 to 1000; 1.1, 2, 4 | Planner and prefetch assumptions about the storage |
| `effective_cache_size` | a quarter, half, three quarters of host memory | Plan choice only; nothing is allocated |

Each replay records every step's wall time and the same resource figures as Phase 0. A crash, an out-of-memory kill or a run that gets slower is recorded as the result, with the database log kept.

## Phase 3: combine and confirm

Combine the settings that helped, check the combination is no worse than its best single setting, and confirm it on two full Scale500k25kGroups runs and one Scale1m60kGroups run, against the baseline from Phase 1. The full runs are the only measure that counts: the replay could miss an effect on the initial load.

## Phase 4: act on the results

- **Customer installs:** change the installer's sizing rules (`size_database` in `deploy/setup.sh`) and the fixed settings in `docker-compose.yml` and `deploy/podman/jim-database.yaml`, keeping the deployment-boot CI check in step.
- **Integration runs:** align `docker-compose.override.yml` and the runner's per-template profiles with what customers get, so scale measurements describe the product.
- **Documentation:** Bundled PostgreSQL Memory in `docs/administration/configuration.md`, the memory sizing in `docs/getting-started/prerequisites.md`, and the reasoning in `engineering/DEVELOPER_GUIDE.md`.
- **Changelog:** an entry for any change to what installs set.

If no setting helps beyond the spread, that is the result: the settings stay, and the evidence says where the time goes instead.

## Success Criteria

- For every step of the leaver scenario, the share of wall time the database is active, and what it waits on.
- For each setting tried, its effect on each step, with the run-to-run spread beside it.
- Any setting that crashes or degrades the database recorded, with how.
- Settings for customer installs and integration runs that are justified by these measurements, or evidence that the current ones are adequate.

## Ordering

The capped-memory runs (testing lower host-memory minimums with the Worker's memory limited) come after Phase 3. Database settings change how much memory the database takes, so the memory minimums are measured once the database settings are decided. Both need the stack to themselves, as does any measurement run for #2669.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| The host's disk stalls make write-related settings look better or worse than they are | Record disk wait with every result; repeat any result that hinges on the disk |
| Run-to-run spread hides small effects | Phase 1's repeated baseline; replays at least twice per setting |
| The replay misses effects on the initial load | Phase 3 confirms on full runs |
| A setting crashes the database or the host | Expected and recorded; the snapshot makes recovery a restore |
| Snapshots take disk space | One pair of volume copies at 500k, kept until Phase 3 ends |
