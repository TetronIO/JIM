# Bounded-Memory Delta Import

- **Status:** Planned
- **Created:** 2026-10-25
- **Author:** Jay
- **Issue:** [#917](https://github.com/TetronIO/JIM/issues/917)
- **Plan:** [BOUNDED_MEMORY_DELTA_IMPORT.md](../plans/BOUNDED_MEMORY_DELTA_IMPORT.md)

## Problem Statement

A Delta Import's memory grows with the number of changes in the run, not with the page it is working on. The Worker reads the Connector's pages one at a time. It then holds every created and updated Connected System Object from every page until the last page has been read, and only then saves them. Each updated object is held with all of its attribute values, which for a group is every member.

The result is that the memory a Delta Import needs is set by the busiest change window it will ever see. That size is out of the administrator's control: a reorganisation, a bulk group clean-up, or a first Delta Import after a long pause. At `Scale100k5kGroups` the delta membership import climbed from about 4 GiB to about 20 GiB in 20 seconds (#917, July 2026). It only passed because #920 lowered the floor it started from.

Two things have changed since then, and neither removes the problem:

- **#2503** pages the OpenLDAP and 389 Directory Server change logs, so the Connector no longer reads the whole log in one call. The Worker still keeps every page.
- **#2507** reports a memory overrun clearly and stops the run before the kernel kills the Worker. The run still fails, and every change it read is thrown away, because nothing is saved until the end.

An administrator who sized JIM for their steady state therefore finds that an unusually large change window cannot be imported at all. The only remedy is more memory, sized for a worst case they cannot predict.

## Goals

- A Delta Import's peak Worker memory is set by the Connector's page size (and the values on that page), not by the number of changes in the run. Verified by running the same Delta Import with 1x and 5x the changes. The peak should stay within 25% of each other, while the time taken scales with the change count.
- The Scale750k40kGroups and Scale100k5kGroups Delta Imports run with a lower measured peak than the post-#2503 baseline taken before implementation.
- No change to what a successful Delta Import leaves in JIM: the same Connected System Objects, attribute values, references, Pending Export outcomes and deletions as today, for every existing Worker test and integration scenario.
- A Delta Import that fails, is cancelled, or is stopped by the memory guard keeps the changes it has already saved. It keeps the watermark it started from, so the next Delta Import reads the remainder (and harmlessly re-reads what was saved).

## Non-Goals

- **Full Import.** It has the same accumulate-then-save shape and sets the memory floor on a first import. It has extra constraints (deletion detection and #2049's Object Type change replacement both need the whole run), so it is tracked as its own follow-up issue. The staging mechanism built here is designed so Full Import can adopt it.
- **Bounding the Connector's own page.** An LDAP entry is fetched whole, so one page holding several 495k-member groups is large before the Worker sees it. That is the Connector's concern: page size, or ranged attribute retrieval where the directory supports it.
- **File-based Connectors.** They return the whole file as one page today; nothing here changes that.
- **Synchronisation runs.** #451 bounded Full and Delta Synchronisation memory separately.
- **Changing the Run Profile safeguards.** The Delta Import deletion limits (#2089) keep their all-or-nothing meaning.

## User Stories

1. As an administrator, I want a Delta Import after a large reorganisation to complete on the same Worker that handles my normal day, so that I do not have to size JIM for a worst case I cannot predict.
2. As an administrator, I want a Delta Import that is stopped part-way (by me, by a failure, or by the memory guard) to keep what it already did, so that the next run finishes the job instead of starting again.
3. As an administrator, I want the Activity to tell me how far a stopped Delta Import got, so that I know whether anything was saved and what the next run will do.

## Requirements

### Functional Requirements

1. **Stage each page.** After the Worker processes a page of a Delta Import, it saves that page's creates and updates and records their execution items. It then reconciles their Pending Exports and releases the page's objects before reading the next page.
2. **Deletion limits keep their meaning.** A Delta Import's Connector-reported deletes are still decided together once every page has been read (#2089). Over the limits, none is applied and the run keeps its starting watermark. Within them, all are applied. Creates and updates are unaffected either way, as today.
3. **A later page's copy of an object still wins.** When an object staged by an earlier page arrives again, the later copy is applied to the object as that earlier page saved it. This holds for a further update, a delete, and a re-insert after a delete. A later copy identical to the one already staged in this run is recognised as unchanged without reloading the object.
4. **References to objects on later pages resolve.** A reference to an object this run creates on a later page is resolved by the end of the run, with the same Object Type scoping and case rules as today (#1285, #2051). A reference still unresolved at the end is reported on its execution item exactly as today (#959), under the Connected System's Unresolved Reference Handling setting.
5. **Every count stays truthful.** The Activity's created, updated, unchanged, deleted and withheld figures count objects, not copies. An object staged on two pages is counted once, under its final outcome.
6. **A stopped run says what it kept.** When a Delta Import fails, is cancelled, or is stopped by the memory guard after staging at least one page, its Activity says how many changes were saved. It also says that the next Delta Import will read from the same starting point.
7. **The watermark rule is unchanged.** The new watermark is recorded only after every page is staged (#1868). Re-reading a staged change must leave JIM exactly as it was, with no new change record or execution item.

### Non-Functional Requirements

- **Memory:** the Worker's peak during a Delta Import is set by one page plus the run-wide lookups that already exist (the external ID lookup, about 200 bytes per Connected System Object). The run-wide state added by this change is limited to identifiers and hashes: no attribute values, no object graphs.
- **Throughput:** a Delta Import of a typical change window (one page or a few) is no slower than today. Large windows may pay a per-page save cost; the end-of-run reference sweep must be one set-based query, not one per value.
- **Integrity:** no partially saved object. Each object's attribute values, change record and execution item are saved together, as today's batches already ensure.

## Examples and Scenarios

### Scenario 1: Large membership change

**Given**: a Connected System with 750k users and 40k groups, and a Delta Import whose change window modified 30k groups
**When**: the Delta Import runs
**Then**: memory rises and falls with each page, the peak is close to that of a 3k-group window, and the result matches today's

### Scenario 2: The same group changed on many pages

**Given**: one 495k-member group modified on 40 different pages of the change log, with no change in between to its membership as fetched
**When**: the Delta Import runs
**Then**: the group is loaded and compared once, and each later copy is recognised as identical to what this run staged. The Activity counts it as one updated object.

### Scenario 3: Deletes over the limit

**Given**: a Run Profile with a deletion limit of 100, and a change window with 5,000 updates followed by 500 deletes
**When**: the Delta Import runs
**Then**: the 5,000 updates are staged page by page and no delete is applied. The Activity warns that 500 deletes were withheld, and the watermark is unchanged, so the next run reads the same window again. That re-read stages nothing new for the updates.

### Scenario 4: Stopped part-way

**Given**: a Delta Import of 20 pages, stopped by the memory guard during page 12
**When**: the Activity is opened
**Then**: it records the stop and that 11 pages of changes were saved. The next Delta Import reads from the original watermark, finds those 11 pages already staged, and stages the remaining 9.

### Scenario 5: A group referencing a user created later in the run

**Given**: page 2 modifies a group to add a member whose user object is created on page 7
**When**: the Delta Import completes
**Then**: the membership resolves to the user's Connected System Object, as it does today, and no unresolved reference is reported

## Constraints

- No new NuGet packages.
- Raw SQL on the hot paths follows `src/CLAUDE.md` (bulk column constants, `RequiresPostgres` round-trip tests, tracked-instance fix-up).
- Released migrations are append-only; this change should need no schema change, and the plan says where it would.
- Synchronisation integrity rules (`src/JIM.Application/CLAUDE.md`): fast failure over corrupted state; every error on an execution item or the Activity.

## Affected Areas

| Area | Impact |
|------|--------|
| Worker | `SyncImportTaskProcessor`: per-page staging, deferred deletes, staged-copy tracking, end-of-run reference sweep, partial-run message |
| Data | A reference sweep scoped to this run's objects; an execution item update for late-reported unresolved references |
| Application | `ImportOutcomeMessage`: wording for a run that stopped after staging |
| Database | None expected |
| API / PowerShell / UI | None: no new setting or field |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/configuration/import-watermarks.md` | A failed, cancelled or stopped Delta Import keeps what it staged; the next run re-reads from the same watermark |
| `docs/administration/troubleshooting-worker.md` | Delta Import memory now scales with the page, not the change window; sizing guidance |
| `docs/connectors/jim-ldap-connector-delta-import.md` | Page size is now the Delta Import's memory control |
| `engineering/notes/RUN_PROFILE_PHASES.md` | Import phases now repeat per page for a Delta Import |

## Dependencies

- #2503 (merged): one change-log page per Connector call. Without it there is only one page to stage.
- #2507 (merged): the memory guard's Stop becomes non-destructive once pages are staged.
- The Scale750k40kGroups re-run on `main`, for the baseline measurement (Phase 0 of the plan).

## Open Questions

1. **A later copy of an object already staged:** keep both execution items (the earlier page's change and the later one), or withdraw the earlier one as #2074 does today? Withdrawing a saved item means deleting its change record, which loses the intermediate state from the object's history. The plan recommends keeping both items while counting the object once; see the plan's Phase 3.
2. **Create on one page, delete on a later one:** today the object is never created. Staged per page, it is created and then marked deleted, and removed by the next synchronisation. The plan recommends accepting this, as it is what two consecutive Delta Imports would do.
3. **Value budget within a page:** add it in this work, or only if Phase 3's measurements show one page alone is too large? The plan recommends measuring first.

## Acceptance Criteria

- [ ] Phase 0 baseline recorded: post-#2503 peak Worker memory for the Scale100k5kGroups and Scale750k40kGroups Delta Imports
- [ ] Peak Worker memory for a Delta Import is within 25% between 1x and 5x the change count (Worker test harness measurement plus one integration run)
- [ ] Every existing Worker test passes unchanged, except tests that assert the open-question behaviours, which are updated deliberately and listed in the PR
- [ ] New tests cover each functional requirement, including `RequiresPostgres` tests for page staging and the reference sweep
- [ ] Scenario 8 (Scale100k5kGroups) and Scale750k40kGroups pass with a lower peak than the baseline
- [ ] A Delta Import stopped part-way keeps its staged pages, keeps its watermark, and its next run completes with no duplicate change records
- [ ] Docs above updated; changelog entries for the memory improvement and the changed stop behaviour

## Additional Context

- #917: the original investigation and July measurements
- #920: hydration fixes that lowered the floor
- #451: bounded-memory synchronisation (the same idea applied to sync runs)
- #1868: the watermark is recorded only once the run has staged what it read
- #2074, #2089, #2049, #959, #1285: the whole-run behaviours this change must preserve
