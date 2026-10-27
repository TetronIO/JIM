# Operations: Unified Activity View

- **Status:** Planned
- **Created:** 2026-10-27
- **Author:** JayVDZ (envisioned and PRD drafted via Claude Code)
- **Issue:** [#2549](https://github.com/TetronIO/JIM/issues/2549)

## Problem Statement

The Operations page splits what the worker is doing across two tabs:

- **Queue** shows what is waiting and what is running, with the high-fidelity progress display (step rail, step name, object count), Schedule Execution grouping, cancel actions and the "Execute a Run Profile" form.
- **History** shows what has finished, plus the in-progress run again, but with a low-fidelity progress bar and none of the queue context.

An administrator who wants to know "what is JIM doing overall?" has to flick between the two tabs repeatedly: Queue to see what is coming and how far the current run has got, History to see what has just finished and how it went. Neither tab answers the question on its own, and the in-progress run is shown twice, at two different levels of detail. In practice this is frustrating, and it gets worse with parallel Schedule steps, where several runs are in progress at once.

A third gap: nothing on the page says when the next Schedule will add work to the queue, so an administrator cannot tell whether the system is idle or about to get busy without opening the Schedules tab.

## Goals

- One tab answers "what is waiting, what is running, and what has finished" without switching tabs.
- In-progress runs are shown once, always with the high-fidelity progress display, including several at once when a parallel Schedule step is running.
- The page reads in time order: Queued (later), In progress (now), Completed (earlier).
- Administrators can narrow the view to any combination of the three states.
- The next Schedule run is visible on the Operations page whether Service Health is expanded or collapsed.

## Non-Goals

- **Timeline view.** A per-Connected-System timeline (past runs, a "now" line, estimated queued runs, Schedule due markers) was mocked up and is parked as a possible optional second view later. It is not part of this work.
- **Showing Schedules that have not fired in the Queued section.** Queued contains only Schedule Executions and tasks actually in the worker queue. Not-yet-fired Schedules would lengthen the page and dilute the section; the next one is surfaced in the Scheduler card instead.
- **Reordering the queue.** No drag-to-reorder or priority changes.
- **Changes to the Schedules, Passwords or Generated Values tabs.**
- **REST API or PowerShell changes.** The queue and history are already available separately through the API and the module (`Get-JIMWorkerTask`, `Get-JIMActivity`); this is a portal presentation change. Surface parity is therefore not applicable, by design.
- **Changes to the Activity detail page or Schedule Execution detail page.**

## User Stories

1. As an administrator, I want to see queued, running and completed work on one page, so that I understand what JIM is doing without switching tabs.
2. As an administrator, I want every in-progress run to show its step, count, rate and time remaining, so that I can judge how long the current work will take.
3. As an administrator, I want to see parallel runs side by side under their Schedule Execution, so that I understand they are running at the same time and belong together.
4. As an administrator, I want to hide states I do not care about (for example only Completed while reviewing outcomes), so that the page shows just what I need.
5. As an administrator, I want to see when the next Schedule is due, so that I know whether the system is about to get busy.

## Requirements

### Functional Requirements

**Tab structure**

1. The Queue and History tabs are replaced by a single **Activity** tab, the first and default tab on the Operations page. Schedules, Passwords and Generated Values are unchanged.
2. Existing deep links keep working: `?t=queue` and `?t=history` resolve to the Activity tab (with `?t=history` pre-selecting the Completed filter only, if that proves useful; see Open Questions). Every internal link and breadcrumb that targets the old tabs is updated to `?t=activity`.

**Sections and order**

3. The Activity tab shows three sections, top to bottom: **Queued**, **In progress**, **Completed**. Each section header shows its name, a count and a time cue (Later, Now, Earlier).
4. All three sections share one column layout so the eye can track down the page. The left-most column, the detail column and the time columns change meaning per section:

   | Section | Column 1 | Detail column | Time columns |
   |---|---|---|---|
   | Queued | Run position (Next, 2nd, ...) | Status (Queued, Waiting for previous step) | Queued, Est. start |
   | In progress | Status (Processing) | Progress (step rail, step name, count, rate) | Started, Remaining |
   | Completed | Result | Outcomes (existing outcome chips) | Executed, Duration |

   Connected System, Run Profile and Initiated By are common to all three.
5. A section with no items shows a one-line empty state (for example "Nothing queued") rather than disappearing, so the page shape stays stable as work moves through.

**Queued**

6. Queued contains only tasks in the worker queue: Schedule Executions (grouped under their header, with step rail, "Open execution" and "Cancel Schedule", as today) and individually queued tasks.
7. Queued is sorted so the **next run sits at the bottom of the section**, immediately above In progress; the furthest-out item is at the top. This keeps the whole page in one time direction. A short hint under the header states the order.
8. Tasks in the same parallel Schedule step share a run position and are marked as parallel (the existing split icon).
9. Each queued item shows an **Est. start** time, estimated from the expected end of the runs ahead of it and each Run Profile's last successful duration. Where no estimate is possible (no previous run), the cell is blank rather than guessed.
10. Queued shows at most 10 rows by default, keeping the rows nearest In progress, with a "Show N more" control for the rest, so a long queue never pushes In progress off screen.
11. Cancel actions behave exactly as on the current Queue tab (tasks waiting for a previous step cannot be cancelled individually; a Schedule Execution can be cancelled as a whole).

**In progress**

12. In progress shows every running task, one row each, using the existing high-fidelity progress components (`WorkerTaskProgress` / `RunPhaseMicroRail` step rail, step position text, object count, rate and remaining time from `RunProgressMetrics`).
13. When the running tasks belong to a Schedule Execution, they are grouped under its header, which states the step and how many tasks are running in parallel (for example "step 2 of 3 · 2 tasks running in parallel").
14. Each running row keeps its cancel action and links to its Activity.
15. When a run finishes it leaves In progress and appears at the top of Completed on the next refresh; it is never shown in two sections at once.

**Completed**

16. Completed is the current History grid without in-progress rows: server-side paging, outcome chips, "Has child activities" and "Scheduled only" options, newest first.
17. The History "Status" filter is renamed **Result** and applies only to Completed (Complete, Complete with warnings, Failed, Cancelled, as the existing values allow). It is visibly disabled when Completed is hidden.

**Filters**

18. Three toggle buttons, **Queued**, **In progress** and **Completed**, each showing its count, control which sections are shown. All are on by default. Any combination is allowed; with all three off, the page shows a one-line prompt to choose one.
19. The existing Connected System, Run Profile, Schedule and search filters apply to all three sections.
20. Toggle and filter state is kept in the URL, as the History tab does today, so a filtered view can be bookmarked and survives a refresh.

**Queueing a Run Profile**

21. The "Execute a Run Profile" form moves into a dialog opened by a **Queue a Run Profile** button in the Activity toolbar. Its behaviour is unchanged.

**Service Health: next Schedule**

22. The Scheduler card shows the next Schedule due to fire: "Next: <Schedule name> <day and time> (in <relative time>)", with the name linking to the Schedule. It is derived from the earliest `NextRunTime` among enabled Schedules.
23. When Service Health is collapsed, its header carries the same information in short form ("next Schedule <day and time>").
24. With no enabled Schedules, the card says "No Schedules due" and the collapsed header omits the phrase.

**Live updates**

25. All three sections update live, using the existing real-time notifications with the existing polling fallback. Refreshing one section must not reset paging, scroll position or expanded "Show more" state in another.

### Non-Functional Requirements

- **Performance:** the combined tab must not poll more often than the two tabs do today, and Completed stays server-side paged. Loading the Activity tab with 10,000+ history rows and a 50-task queue must remain responsive.
- **Accessibility:** toggles are real buttons with `aria-pressed`; section headings are real headings; status is never conveyed by colour alone (each state has a word and an icon).
- **Responsive:** at phone width the toolbar wraps and each section's grid scrolls horizontally inside its own box.

## Examples and Scenarios

The interactive mock-up (concept A) is on the design canvas: https://claude.ai/artifact/6MkTe5DsXQs3KCYxgSJtJF

```
Operations
[Service Health: Worker · Sync  2 tasks in parallel | Worker · Passwords  Idle | Scheduler  Idle · Next: Weekly Full Reconciliation Sun 02:00 (in 3 d 11 h)]
[ACTIVITY] SCHEDULES  PASSWORDS  GENERATED VALUES

Show: (Queued 3) (In progress 2) (Completed 8)                 [QUEUE A RUN PROFILE]
[Connected System v] [Run Profile v] [Schedule v] [Result (Completed only) v] [Search]

Queued 3  LATER  Furthest out at the top; the next run sits just above In progress
  2nd   Yellowstone APAC  Full Import (Scoped)  Queued                     Infrastructure Key   4 min ago   ~18:45  (x)
  -- Regional Sync Cycle · step 3 of 3 waits for step 2 --------------------------------------------- Open execution
  Next  Glitterband EMEA  Export   Waiting for previous step  (parallel)   Regional Sync Cycle  32 min ago  ~17:40
  Next  Yellowstone APAC  Export   Waiting for previous step  (parallel)   Regional Sync Cycle  32 min ago  ~17:40

In progress 2  NOW
  -- Regional Sync Cycle · step 2 of 3 · 2 tasks running in parallel ------------------- Open execution  (x)
  Processing  Glitterband EMEA  Full Sync  [##|//|  |  ] Step 2 of 4: Processing Connected System Objects 28,500 / 790,007  68 / s     7 min ago  ~3 h 6 min  (x)
  Processing  Yellowstone APAC  Full Sync  [##|##|//|  ] Step 3 of 4: Resolving cross-page references 482,100 / 790,007   7 min ago  ~5 min      (x)

Completed 8  EARLIER  Most recent first                     [ ] Has child activities  [ ] Scheduled only
  Complete  Glitterband EMEA  Delta Import  (edit 1,204)                   Regional Sync Cycle  32 minutes ago  ~24 min
  Complete  Yellowstone APAC  Delta Import  (add 312) (edit 2,877)         Regional Sync Cycle  32 minutes ago  ~19 min
  ...
```

### Scenario 1: Getting an overview

**Given**: a Schedule Execution is on a parallel step with two Full Syncs running, two Exports waiting for the next step, and one manually queued import
**When**: the administrator opens Operations
**Then**: the Activity tab shows the import and the two Exports in Queued (Exports at the bottom, marked Next and parallel), both Full Syncs in In progress under the Schedule Execution header with full progress detail, and recent runs in Completed, all without switching tabs

### Scenario 2: Reviewing outcomes only

**Given**: the Activity tab with all sections shown
**When**: the administrator turns off Queued and In progress, and sets Result to Failed
**Then**: only failed completed runs are shown, and the URL reflects the filter so the view can be bookmarked

### Scenario 3: A run finishes

**Given**: a Full Sync is in In progress
**When**: it completes
**Then**: on the next live update it disappears from In progress and appears at the top of Completed with its outcome chips; the next queued task moves into In progress

### Scenario 4: Long queue

**Given**: a Schedule Execution with 24 queued steps
**When**: the administrator opens the Activity tab
**Then**: Queued shows the 10 rows nearest In progress and "Show 14 more"; In progress is visible without scrolling on a 1080p display

### Scenario 5: Next Schedule with Service Health collapsed

**Given**: Service Health is collapsed and "Weekly Full Reconciliation" is the next enabled Schedule, due Sunday at 02:00
**When**: the administrator views Operations
**Then**: the collapsed header reads "... next Schedule Sun 02:00", and expanding it shows the full Scheduler card text with a link to the Schedule

### Scenario 6: Old links

**Given**: a bookmark to `/admin/operations?t=history`
**When**: the administrator opens it
**Then**: the Activity tab opens

## Constraints

- No new third-party dependencies; build from MudBlazor and the existing shared components (`WorkerTaskProgress`, `RunPhaseMicroRail`, `RunProgressMetrics`, `ScheduleStepRail`, `OutcomeSummaryChips`, `ServiceHealthStrip`).
- UI only calls `JimApplication`; any new query (for example the next Schedule run, or last-duration estimates) goes through the application layer.
- British English, Title Case JIM entity names, no em dashes in UI text.
- Pre-release: the Queue and History tabs are removed outright, not kept alongside.

## Affected Areas

| Area | Impact |
|------|--------|
| Database | None expected. `Schedule.NextRunTime` already exists. Est. start may need last-successful-duration per Run Profile; check whether an index is needed |
| API | None |
| Worker | None |
| Application | Possibly a method for the next due Schedule and for last-successful durations per Run Profile |
| UI | `Operations.razor` tab strip; new Activity tab replacing `OperationsQueueTab.razor` and `OperationsHistoryTab.razor`; `ServiceHealthStrip.razor` / `ServiceHealthDisplay`; links in `ActivityDetail.razor` and elsewhere that target `?t=history` or `?t=queue` |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/configuration/operations.md` | Rewrite the Queue and History sections as one Activity section: sections, order, toggles, Result filter, Queue a Run Profile dialog, next Schedule in Service Health |
| `docs/configuration/activities.md` | Update references to the History tab |
| `docs/configuration/schedules.md` | Update references to the Queue and History tabs |

## Open Questions

1. Should `?t=history` open the Activity tab with only Completed selected (preserving the old intent), or with everything shown?
2. Est. start: is "last successful duration" a good enough estimator, or should it use a median of recent runs to damp outliers?
3. Should the Queued "Show more" cap be 10, or sized to the viewport?

## Acceptance Criteria

- [ ] The Operations page has an Activity tab in place of Queue and History, and it opens by default
- [ ] Sections appear in the order Queued, In progress, Completed, with counts and time cues
- [ ] Queued shows only queued tasks and Schedule Executions, sorted with the next run nearest In progress, capped with "Show more"
- [ ] Every in-progress task shows the high-fidelity progress display; parallel tasks appear together under their Schedule Execution
- [ ] Completed is paged server-side and excludes in-progress runs; the Result filter applies to it alone
- [ ] Queued / In progress / Completed toggles work in any combination and persist in the URL
- [ ] Connected System, Run Profile, Schedule and search filters apply to all sections
- [ ] Queue a Run Profile opens the existing form in a dialog
- [ ] The Scheduler card and collapsed Service Health header show the next Schedule due
- [ ] Old `?t=queue` and `?t=history` links open the Activity tab; internal links are updated
- [ ] Live updates move a finished run from In progress to Completed without resetting other sections
- [ ] Public docs updated as listed under Documentation Impact

## Additional Context

- Design canvas with concepts A (this PRD), B (Timeline, parked) and C (minimal Now strip, not chosen): https://claude.ai/artifact/6MkTe5DsXQs3KCYxgSJtJF
- Parallel execution: the worker dispatches all tasks of a parallel Schedule step together (`JIM.Worker/Worker.cs`), which is why In progress must handle several rows.
- Related closed work: #246 (progress indication on Operations), #1162 (step-model progress bars), #1196 (Schedule Execution context in the portal).
