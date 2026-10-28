# Operations: Unified Activity View

- **Status:** Done
- **Issue:** [#2549](https://github.com/TetronIO/JIM/issues/2549)
- **PRD:** [PRD_OPERATIONS_UNIFIED_ACTIVITY_VIEW.md](../../prd/done/PRD_OPERATIONS_UNIFIED_ACTIVITY_VIEW.md)

## Overview

Replace the Operations page's Queue and History tabs with one Activity tab showing three sections in time order: Queued, In progress, Completed. Every running task gets the high-fidelity progress display, including several at once on a parallel Schedule step. Queued sorts the next run nearest In progress, shows an estimated start time and sizes itself to the viewport. The Service Health Scheduler card, and the collapsed Service Health header, show the next Schedule due.

This is a portal-only change. Queue and history are already available separately through REST (`/api/v1/worker-tasks`, `/api/v1/activities`) and PowerShell (`Get-JIMWorkerTask`, `Get-JIMActivity`), so surface parity does not apply.

## Business Value

- Administrators see everything the worker is doing on one page, instead of flicking between two tabs.
- Parallel Schedule steps become legible: the runs that are happening together are shown together.
- Estimated start times and the next Schedule due answer "when will this happen?" without opening another tab.

## Technical Architecture

### Current state

- `Pages/Admin/Operations.razor` hosts `NavigableMudTabs`, which derives each tab's `?t=` key by slugifying the panel text (`Queue` becomes `queue`, `History` becomes `history`). It loads the `ServiceHealthReport` for `ServiceHealthStrip` on its own poll.
- `Components/OperationsQueueTab.razor` (859 lines):
  - **Data:** loads `jim.Tasking.GetWorkerTaskHeadersAsync()`, which returns every queued, waiting and processing task, and `ScheduleExecutionProgress` per Schedule Execution via `jim.Activities.GetScheduleStepOutcomesAsync`.
  - **Display:** draws one grid per Schedule Execution plus one for ad-hoc tasks, with `ScheduleStepRail`, `WorkerTaskProgress` (step rail, step position, count) and cancel actions.
  - **Execute form:** hosts the "Execute a Run Profile" form.
  - **Demo mode:** `?d=true` supplies demo data, and `ThemePreview.razor` links to it.
  - **Refresh:** subscribes to `UiNotifications.WorkerTaskChanged` and `ActivityProgressChanged`, with a polling fallback (30 s with real-time, 1 s without).
- `Components/OperationsHistoryTab.razor` (1,168 lines):
  - **Grid:** `VirtualisedDataGrid<Activity>` fed by `jim.Activities.GetActivitiesRangeAsync` (windowed, server-side), including in-progress Activities with a basic progress bar.
  - **Filters:** Connected System, Run Profile, Schedule, Status, Initiated By, Has child activities, Scheduled only; filter state lives in the URL.
  - **Refresh:** same notification subscription, with its own change-detecting poll.
- Rate and time remaining for a running Activity come from the singleton `IActivityEtaTracker.RecordSample(activityId, processed, total)`, already shared by `ActivityDetail.razor` and the REST progress endpoint. The Queue tab does not use it today.
- `Schedule.IsEnabled` and `Schedule.NextRunTime` exist; `SchedulerServer` maintains `NextRunTime`.
- `Activity.ExecutionTime`, `Activity.ConnectedSystemRunProfileId` and `Activity.Status` are enough to compute recent durations per Run Profile.
- The only callers of the old tab keys are the Operations breadcrumbs in `ActivityDetail.razor:1381` and `ActivityRunProfileExecutionItemDetail.razor:625` (`?t=history`).

### Proposed solution

```
Operations.razor
├─ ServiceHealthStrip (Report, NextSchedule)        ← next Schedule on the Scheduler card and the collapsed header
└─ NavigableMudTabs
   ├─ Activity  → OperationsActivityTab.razor       ← new, replaces Queue + History
   │   ├─ toolbar: section toggles, filters, "Queue a Run Profile" → QueueRunProfileDialog
   │   ├─ Queued section      ┐ one WorkerTaskHeader load + one notification/poll loop
   │   ├─ In progress section ┘
   │   └─ Completed section   ← VirtualisedDataGrid<Activity>, InProgress excluded
   ├─ Schedules / Passwords / Generated Values (unchanged)
```

Display logic that decides what is shown goes into plain C# classes under `src/JIM.Web/Models/`, so it can be unit tested without rendering:

| Class | Responsibility |
|---|---|
| `QueueDisplayOrder` | Turns the non-processing `WorkerTaskHeader`s into display rows: Schedule Execution groups and ad-hoc tasks, run positions (`Next`, `2nd`, ...), parallel tasks sharing a position, emitted furthest-first so the next run is last (nearest In progress). |
| `QueueStartEstimator` | Given the running tasks' remaining seconds and the queued items in run order with their Run Profiles' median durations, returns an estimated start per item. A parallel step starts when its slowest predecessor ends. An unknown duration blanks that item and every item after it. |
| `QueueRowBudget` | Given the measured viewport height, the Queued section's top offset, the row height and the In progress header plus first-row height, returns how many Queued rows to show (floor 3), keeping the rows nearest In progress. |
| `ActivitySectionState` | The three section toggles and the Result filter: parse and format to the URL (for example `s=queued,running,completed`), whether Result is enabled, and the "nothing selected" state. |
| `ServiceHealthDisplay.NextSchedule*` | The Scheduler card line ("Next: {name} {Sun 02:00} (in 3 d 11 h)"), the collapsed header phrase ("next Schedule Sun 02:00") and the "No Schedules due" case. Extends the existing static class. |

### Data flow

1. **Worker tasks:** one `GetWorkerTaskHeadersAsync` call per refresh feeds both Queued (`Queued` and `WaitingForPreviousStep`) and In progress (`Processing`). Each processing row also samples `IActivityEtaTracker` by `ActivityId` for rate and remaining.
2. **Durations:** `jim.Activities.GetMedianRecentDurationsAsync(runProfileIds, sampleSize: 5)` is called when the set of queued Run Profiles changes, not on every tick, and cached in the component.
3. **Next Schedule:** `jim.Scheduler.GetNextDueScheduleAsync()` loads on the page's existing Service Health poll.
4. **Completed:** `GetActivitiesRangeAsync` with a status filter that always excludes `InProgress`. When Result is set, its values are intersected with the completed statuses.

## Implementation Phases

### Phase 1: Application queries

TDD, application tests alongside the existing `ActivityServer` / `SchedulerServer` tests, plus a database-tier test (`jim-test-db`) for the repository query, because the in-memory provider would hide a translation or ordering fault.

- `ActivityServer.GetMedianRecentDurationsAsync(IEnumerable<int> runProfileIds, int sampleSize = 5)` returns `Dictionary<int, TimeSpan>`.
  - "Successful" means `Complete` and `CompleteWithWarning`.
  - Takes the most recent `sampleSize` per Run Profile by `Executed`; the median is computed in the application layer.
  - The repository returns the last N durations per profile; a window function via raw SQL with parameters is acceptable if EF cannot express it.
  - Check the query plan; add an index on (`ConnectedSystemRunProfileId`, `Status`, `Executed`) only if it is needed (new migration).
- `SchedulerServer.GetNextDueScheduleAsync()` returns the enabled Schedule with the earliest non-null `NextRunTime` (id, name, time), or null.
- Tests:
  - Median calculation for odd, even and fewer-than-5 counts.
  - Failed and cancelled runs are ignored.
  - No-history profiles are absent from the result.
  - Disabled Schedules are ignored.

### Phase 2: Display logic

TDD, plain NUnit in `test/JIM.Web.Tests/`.

- `QueueDisplayOrder`: ad-hoc only; a Schedule Execution with sequential and parallel steps; mixed; the next run is last; parallel tasks share a position.
- `QueueStartEstimator`: no running task; one running task with known and unknown remaining; parallel predecessors (the max wins); an unknown median blanks downstream items.
- `QueueRowBudget`: tall viewport shows everything; short viewport clamps to the floor of 3; nearest-to-In-progress rows are kept; the "Show N more" count is right.
- `ActivitySectionState`: URL round trip; defaults to all on; all off; Result disabled when Completed is hidden.
- `ServiceHealthDisplay`: next Schedule wording, collapsed phrase, none due. These extend `ServiceHealthDisplayTests`.

### Phase 3: Activity tab

- Build `Components/OperationsActivityTab.razor`:
  - **Queued and In progress:** taken from `OperationsQueueTab.razor`. Reuse `WorkerTaskProgress`, `ScheduleStepRail` and `RunProgressMetrics`; the latter gets rate and remaining from `IActivityEtaTracker`. Cancel and "Cancel Schedule" behave as today.
  - **Completed:** taken from `OperationsHistoryTab.razor`. Keep `VirtualisedDataGrid` windowed loading; this replaces the "Rows per page" pager drawn in the mock-up, and the behaviour is otherwise as in the PRD. Exclude `InProgress`. Rename Status to Result and disable it when Completed is hidden.
  - **Columns:** one shared CSS grid column template across the three sections, in the tab's scoped stylesheet.
  - **Toggles:** real `MudToggleIconButton`s or buttons with `aria-pressed`, each with its count; state in the URL via `ActivitySectionState`.
  - **Filters:** Connected System, Run Profile, Schedule and search filter all three sections. Queued and In progress filter client-side, since the full task list is already loaded; Completed filters server-side, as today.
  - **Empty states:** a one-line empty state per section, and a prompt when all toggles are off.
  - **Refresh:** a single subscription to `UiNotifications` and a single poll loop. Refreshing worker tasks never reloads Completed's window, and refreshing Completed preserves its scroll position, as `RefreshIfChangedAsync` does today.
    - *As built:* Queued and In progress share one loop and Completed keeps its own, each carried over from the tab it came from. Merging them bought nothing: they read different things on different triggers, and keeping them apart is what guarantees a queue refresh never reloads Completed's window.
  - **Demo mode:** keep `?d=true`, extending the demo data with a parallel step so the In progress grouping can be previewed.
- Move the Execute form into `Components/QueueRunProfileDialog.razor`, opened by "Queue a Run Profile". Its logic is unchanged.
- Viewport sizing: add `jimInterop.observeViewport(dotNetRef, element)` to `wwwroot/js/interop.js`. It reports the viewport height and the Queued section's top offset on first render and on a debounced resize (about 150 ms), and the component calls `QueueRowBudget`. Before the first measurement (prerender), use a provisional budget of 6 rows.
- `Operations.razor`: replace the Queue and History panels with `<MudTabPanel Text="Activity">` as the first tab. The `activity` key falls out of `NavigableMudTabs` slugifying.
- Delete `OperationsQueueTab.razor` and `OperationsHistoryTab.razor` (and any scoped CSS). Port the behaviours their tests cover (`OperationsQueueTabTests`, `OperationsHistoryTabTests`) into `OperationsActivityTabTests`, and delete the old test files.
- bUnit tests for `OperationsActivityTab` cover what is shown under which condition, not presentation:
  - toggles hide and show sections
  - an in-progress Activity never appears in Completed
  - parallel processing tasks render under one Schedule Execution header
  - the Result filter is disabled with Completed hidden

### Phase 4: Service Health next Schedule

- Add a `NextSchedule` parameter to `ServiceHealthStrip`, and render it on the Scheduler card and in the collapsed header via `ServiceHealthDisplay`.
- `Operations.razor` loads it with the report on the existing poll.
- Extend `ServiceHealthStripTests`: the card shows the next Schedule with a link; the collapsed header carries it; none due shows "No Schedules due".

### Phase 5: Links, docs, changelog

- Update the two breadcrumbs to `/admin/operations?t=activity`, and grep `src/` and `docs/` for `t=queue` and `t=history` to catch anything added since.
- Docs:
  - `docs/configuration/operations.md`: rewrite the Queue and History sections as one Activity section, and cover the next Schedule in Service Health.
  - `docs/configuration/activities.md`, `docs/configuration/schedules.md`: update tab references.
  - Follow `engineering/DOCUMENTATION_STANDARD.md`.
- Changelog: one 🔄 entry under `[Unreleased]` Changed, for example "🔄 The Operations page now shows queued, running and completed work together on one Activity tab, with full progress for parallel runs and the next Schedule due (#2549)".

### Phase 6: Validation

- `dotnet build JIM.sln` and `dotnet test JIM.sln` with zero warnings; run `jim-test-db` for the Phase 1 query.
- Runtime check on the sandbox stack (`Start-SandboxStack.ps1`), driven with Playwright:
  - queue a Schedule with a parallel step and confirm two In progress rows under one header
  - confirm a finished run moves to Completed without a reload
  - confirm Est. start values and the viewport cap on a short window
  - confirm the next Schedule shows on the card and in the collapsed header
  - confirm the breadcrumbs land on the Activity tab
- Screenshots in a Claude Artefact for review, per the UI change rule.

## Success Criteria

The PRD's acceptance criteria, all ticked, with build, test and the runtime check above passing.

## Benefits

- **UX:** one place to answer "what is JIM doing", time-ordered, with parallel runs legible.
- **Architecture:** two tabs (about 2,000 lines) consolidate into one, with their ordering and estimating logic moved into tested plain classes.
- **Performance:** one worker-task poll instead of two independent ones while the page is open.

## Dependencies

No new packages. MudBlazor, the existing shared components, and the existing `jimInterop` script.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Completed's virtualised grid sits below two variable-height sections; if it relies on page scroll, its window maths could drift as Queued and In progress resize | Give Completed a `MaxHeight` scroll container (as `VirtualisedDataGrid` supports), or verify the grid tolerates offset changes; settle this in Phase 3 before styling |
| Est. start looks precise but is a heuristic | Show it as "~17:40" with a tooltip naming the basis (median of the last 5 successful runs); blank rather than guess |
| Viewport measurement differs between prerender and interactive render | Use a provisional budget of 6 until measured; recalculate only on resize, never on data refresh |
| Median query cost on large Activity tables | Bounded to the queued Run Profiles, cached until that set changes; index only if the plan shows a sequential scan |
| Porting two large components loses edge-case behaviour (demo mode, URL filter state, cancellation guards) | Port the existing tab tests before deleting the old components; diff behaviours explicitly in review |
| `IActivityEtaTracker` is shared with the Activity page and API | It is keyed by Activity and designed for several samplers; no change needed, but confirm rates look sane with the Activity page open at the same time |
