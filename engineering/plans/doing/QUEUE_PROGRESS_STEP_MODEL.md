# Queue Progress: Uplift to the Step Model

- **Status:** Doing (Phase 1 underway)
- **Issue:** [#1162](https://github.com/TetronIO/JIM/issues/1162)
- **Builds on:** [#454](https://github.com/TetronIO/JIM/issues/454) (Run Profile phases), design note [`engineering/notes/RUN_PROFILE_PHASES.md`](../../notes/RUN_PROFILE_PHASES.md)

## Overview

[#454](https://github.com/TetronIO/JIM/issues/454) gave a Run Profile execution a recorded list of steps and drew them on the Activity detail page as a horizontal stepped rail. Everywhere else in the portal that shows progress still shows a bare bar and a count, so the same run reads two different ways depending on which page an administrator is looking at.

This plan closes that gap on the two surfaces where it costs the most: the **per-task Progress cell** and the **Schedule Execution group header**, both in Operations > Queue.

The visual direction was settled from side-by-side mock-ups of four options per surface, reviewed on 2026-08-04. The decisions below are the outcome of that review; they are recorded here because the reasoning is not recoverable from the code.

## Business Value

An administrator watching a run in the queue today can see that something is happening and how many objects it has got through, but not *what* it is doing or *how much of the run is left*. Two specific failures:

- **The count is unscoped.** A run reports "12,480 / 40,000" without saying which of its eight steps that measures. Each counting step resets the total, so the same figure means something different at different moments and there is nothing on screen to say which.
- **A Schedule Execution group header shows arithmetic, not progress.** "6 tasks across 6 steps, 1 processing, 3 queued, 2 waiting" requires the reader to add up six numbers to work out that the Schedule is a third of the way through, and says nothing at all about whether anything has failed.

## Design Decisions

### Per-task Progress cell: Option D, top-aligned

Three stacked elements in the Progress cell:

1. **A segmented micro-rail**: one segment per top-level phase, coloured by that phase's status (completed / skipped / failed / not reached), the running one striped.
2. **The running step's own progress bar**, with its percentage: today's `MudProgressLinear`, unchanged.
3. **A caption**: "Step 6 of 8: Saving changes — 12,480 / 40,000".

**Why not the rail alone.** Equal-width segments imply equal work, and a Run Profile's phases are wildly unequal: "Processing imported objects" is most of an import's wall-clock, "Recording results" is a blink, and "Exporting deferred changes" is usually skipped entirely. A rail alone, sitting in a column headed Progress, reads as a progress bar and would routinely be read as five-eighths done when it is nowhere near. The bar underneath removes the ambiguity by giving the reader the real magnitude of the thing they are actually waiting on.

**The two geometries carry different denominators on purpose: the segments are the run, the bar is the step.** The caption names the step, which is what stops them being conflated. This is the same division the Activity page already makes, which is the point: the queue becomes a smaller view of the same idea rather than a second idea.

**Why top-aligned.** With cells centred, a row's Progress content is three lines tall while every other column is one, so the rail floats at an arbitrary height that shifts with the cell's contents. Top-aligning every cell puts the rail on a known line and lets the eye run down the Name column and across. It also stops rows of unequal height (a failed task has no bar) from looking ragged.

The alignment is **an offset, not a box**: the Progress cell's first element carries a 7px top margin, which is half the difference between a 20px text line and the 6px rail, so its centre lands on the first text line's centre. A one-text-line-tall box with the rail centred inside it would leave the same dead space *below* the rail, widening the gap to the bar and making the cell look airier than it should. The rule says "first element" rather than "the rail" because a phase-less task has no rail, and its bar takes the line instead so the alignment is unbroken across every row.

**Degradation is not a branch.** Clearing Connected System Objects, example data generation and factory reset are not Run Profile executions and have no phases. They render exactly as today, because the rail simply is not there.

### Schedule Execution group header: Option C, with P3 for parallel steps

A rail across the group header's spare horizontal space, between the Schedule name and the Cancel button: one marker per Schedule step, coloured by that step's task status, with the step name underneath.

The equal-work objection above does not apply here. A segment is one whole Run Profile execution, so the unit is honest, and the data is already on the loaded `WorkerTaskHeader`s (`ScheduleStepIndex`, execution mode, per-task status). No schema change and no extra query.

**Parallel steps use P3: one marker, divided into a wedge per task, at 16px.** Four treatments were mocked. The deciding case is a parallel step where one task has failed and one has succeeded, because that is precisely when someone is reading this header:

| | Treatment | Verdict |
|---|---|---|
| P1 | One node with the `CallSplit` glyph | Cheapest, and cannot show the asymmetry at all. Ruled out. |
| P2 | The rail forks and rejoins, a row per task | Unambiguous, but row height scales linearly with the fan-out. A Schedule running a dozen imports concurrently turns one group header into a twelve-row block. **Ruled out for a list view; reserved for a future Schedule Execution detail page.** |
| P3 | One marker, divided into a wedge per task | **Chosen.** Carries the asymmetry, and its height is constant regardless of the fan-out. |
| P4 | One rail position, a lane per task | Legible, but has P2's problem in milder form: twelve tasks is twelve lanes. |

**The wedge ordering rule is load-bearing.** Wedges are ordered **by status, not by task**: failed, then completed, then running, then not started, clockwise from twelve o'clock. Ordering by task index would scatter a lone failure anywhere around the disc and make it invisible at one-twelfth. Ordering by status means a failure always starts at twelve o'clock and always reads, and the marker degrades gracefully from "a wedge per task" to "a proportion" exactly as the task count grows past what discrete wedges can carry. That degradation is the right one: at twelve tasks the reading left is "mostly done, one failure", which is the reading that matters.

Sized at 16px rather than the rail's usual 12px: a parallel marker carries more than a single-task marker and can afford to.

### Out of scope

- **Operations > History.** The same rail against terminal data, with per-step durations. Worth doing; not worth blocking the live surfaces on. Separate issue.
- **A Schedule Execution detail page.** Does not exist today. It is where P2 belongs, with a fork row per parallel task carrying its own progress bar, and it would give the History tab somewhere to link to. Separate issue.
- **`ExampleDataTemplateDetail.razor`.** Uses the same `WorkerTaskProgress` component, so it inherits the change for free; example data generation has no phases, so it renders exactly as today. No work, but it must be checked.

## Technical Architecture

### Current state

| Concern | Where it lives |
|---|---|
| Phase declaration per run type | `RunProfilePhaseCatalogue` (JIM.Models) |
| Phase state | `ActivityPhaseSet`, `ActivityPhase` (JIM.Models) |
| Reading a run's steps (top-level, active, position) | `RunPhaseReading` (JIM.Models) |
| Status → fill / colour / icon / tooltip | **Inline inside `RunPhaseStepper.razor`** |
| Phase icons | `RunPhaseIcons` (JIM.Web/Shared) |
| Queue rows | `WorkerTaskHeader` → `OperationsQueueTab.razor` → `WorkerTaskProgress.razor` |

`RunPhaseReading` already exists precisely so the progress API, the stepper and the Activity readout cannot disagree about "step 2 of 3". The **status-to-appearance** rules have no equivalent: they are private methods on `RunPhaseStepper`. This plan adds two more consumers of those rules, so extracting them is a prerequisite, not a tidy-up.

### Proposed structure

```
JIM.Models/Activities/
  RunPhaseReading.cs           (existing; unchanged)
  ScheduleStepReading.cs       NEW - groups WorkerTaskHeaders into Schedule steps,
                                     derives each step's aggregate status and the
                                     status-ordered wedge proportions for P3

JIM.Web/Shared/
  RunPhaseVisuals.cs           NEW - status -> colour token, fill %, icon, CSS modifier,
                                     outcome tooltip. Extracted from RunPhaseStepper.
  RunPhaseStepper.razor        MODIFIED - consumes RunPhaseVisuals
  RunPhaseMicroRail.razor      NEW - the segmented rail for a table cell
  ScheduleStepRail.razor       NEW - the group-header rail, incl. P3 markers
  WorkerTaskProgress.razor     MODIFIED - rail + bar + caption, degrades when no phases
```

`ScheduleStepReading` goes in JIM.Models rather than JIM.Web because the REST and PowerShell surfaces need the same grouping and aggregate-status rules (see Phase 4), and a rule duplicated across three surfaces is the exact failure `RunPhaseReading` exists to prevent. It returns data, not colours; `RunPhaseVisuals` stays in JIM.Web because colours and icons are presentation and JIM.Models has no business knowing about MudBlazor.

### Data flow

`WorkerTaskHeader` gains four fields:

```csharp
public string? CurrentStepName { get; set; }
public int? CurrentStepNumber { get; set; }   // 1-based, among top-level phases
public int? TotalSteps { get; set; }
public List<WorkerTaskPhaseSummary> Phases { get; set; } = [];  // top-level only
```

`WorkerTaskPhaseSummary` is a new DTO in `JIM.Models/Tasking/DTOs/`: `Order`, `Name`, `Status`. Enough to draw a segment and title its tooltip; deliberately not the full `ActivityPhase`.

**Only top-level phases are carried.** A Connector's phases are detail inside the step that called it; including them would make the segment count (and therefore "step N of M") differ between two runs of the same Run Profile depending on which Connector was in use. `RunPhaseReading.TopLevel` is the filter, applied server-side so the client cannot get it wrong.

The three scalars are derived server-side from `RunPhaseReading.ActiveTopLevel` / `PositionOf`, matching what the Activity progress API already reports. They are carried as scalars rather than recomputed in the portal so PowerShell and REST get the identical sentence for free.

**Query cost is bounded.** `WorkerTask` rows are deleted once their work completes (`TaskingServer`/`SchedulerServer` both call `DeleteWorkerTaskAsync`), so `GetWorkerTaskHeadersAsync` only ever sees in-flight work: at most a few dozen rows, each with roughly ten phase rows. Progress is push-based (#307), so a queue row carrying phases updates live on the existing `ActivityProgressChanged` channel with no new plumbing.

## Implementation Phases

### Phase 1: Extract the status-to-appearance rules ✅

Prerequisite for everything else, and the issue calls it out explicitly.

- Create `JIM.Web/Shared/RunPhaseVisuals.cs`: `HasRun`, `StatusModifier`, `StatusIcon`, `FillPercent`, `OutcomeTooltip`, lifted from `RunPhaseStepper.razor`.
- Rewrite `RunPhaseStepper.razor` to call it. No visual change; the Activity page must render identically.
- Tests (`test/JIM.Web.Tests/RunPhaseVisualsTests.cs`): a table-driven test over every `ActivityPhaseStatus` asserting modifier, icon and fill. Red-first by writing the test against the new class before it exists.

**A second copy already existed.** `RunProgressMetrics.razor` carried its own `StatusModifier` and `StatusIcon`, added when the readout beneath the rail needed to draw the steps inside the running step. The two had not drifted yet, but the readout's `StatusIcon` had already dropped the null-phase arm, so they were no longer the same function. Both now call `RunPhaseVisuals`, which makes the queue rail the third consumer rather than the second and moves the extraction from "worth doing" to "should have happened at #454".

`SetPasswordDialog.razor`'s rail is deliberately left alone: it shares the phase stepper's *CSS* for its markers, but its states are password-set outcomes rather than `ActivityPhaseStatus`, so it is not a consumer of these rules.

**Done when:** the Activity page is pixel-identical and no Razor component contains status-to-appearance logic. ✅

### Phase 2: Carry the steps to the queue

- Add `WorkerTaskPhaseSummary` (JIM.Models) and the four fields on `WorkerTaskHeader`.
- `TaskingRepository.GetWorkerTaskHeadersAsync`: add `.ThenInclude(a => a.Phases)`, project through `RunPhaseReading.TopLevel`, populate the scalars.
- Tests: repository-level coverage that a Run Profile task carries its top-level phases and correct step scalars, and that a Connector's phases are excluded. A `RequiresPostgres` test is warranted here because the in-memory provider auto-tracks navigation properties and would mask a missing `Include`.

**Done when:** a queue header for a running import carries eight phases and "step 6 of 8: Saving changes", and one for a Clear Connected System Objects task carries none.

### Phase 3: The per-task Progress cell

- New `RunPhaseMicroRail.razor`, driven by `RunPhaseVisuals`.
- `WorkerTaskProgress.razor`: accept the new parameters, render rail + bar + caption, fall through to today's markup when `Phases` is empty.
- `OperationsQueueTab.razor`: pass the parameters; `vertical-align: top` on the row template.
- `site.css`: `.jim-queue-progress-*` classes, including the 7px first-element offset. Every class named in markup must exist in the stylesheet (see `JIM.Web/CLAUDE.md`); grep after writing.
- **The caption replaces the worker's progress message.** A message that only restates the running step's name is noise, which is the rule `RunProgressMetrics` already follows on the Activity page. Worker-side messages that would only restate the step should be `string.Empty`.
- Tests (bUnit, `test/JIM.Web.Tests/`): a task with phases renders a rail with one segment per phase and the running one marked; a task without phases renders today's bar and no rail; the caption names the step.

**Done when:** the queue matches the approved mock-up, and `ExampleDataTemplateDetail.razor` is visually unchanged.

### Phase 4: The Schedule Execution group header

- `ScheduleStepReading` (JIM.Models): group `WorkerTaskHeader`s by `ScheduleStepIndex`, derive each step's aggregate status, and for a parallel step return status-ordered wedge proportions.
- New `ScheduleStepRail.razor` consuming it; P3 markers rendered as a `conic-gradient` built from the proportions.
- `OperationsQueueTab.razor` group header template: the rail between the Schedule name and the Cancel button, with the Cancel button pushed right so it lands under Actions.
- Tests: **the wedge-ordering rule gets its own unit test** (failed first from twelve o'clock, then completed, running, pending) at 2, 3, 6 and 12 tasks. It is the rule that makes P3 work at scale and the one most likely to be "simplified" later by someone who does not know why it is there.

**Done when:** a Schedule with a parallel step showing one failed and one succeeded task renders a two-tone marker with the failure at twelve o'clock.

### Phase 5: Surface parity (REST + PowerShell)

Required in the same PR: this is a read feature, so read parity applies.

- **REST:** the worker-task DTOs in `WorkerTasksController` gain the step scalars and the phase list. `ScheduleExecutionsController` gains a step-progress read using `ScheduleStepReading`.
- **PowerShell:** `Get-JIMWorkerTask` output shape gains the step fields; a display helper mirroring `Get-JIMActivityProgressDisplay` so the sentence is identical across all three surfaces.
- Tests: `test/JIM.Web.Api.Tests/` for the DTOs, Pester for the cmdlets.
- Docs: update the REST and PowerShell reference pages under `docs/`.

### Phase 6: Documentation and changelog

- `CHANGELOG.md` under `[Unreleased]` → `### Added` (user-facing UI change).
- Public docs under `docs/` for the changed queue behaviour (`changelog-lint` enforces this for a `✨` entry).
- Update `engineering/notes/RUN_PROFILE_PHASES.md` with the second and third consumers of the phase model.
- Move this plan to `engineering/plans/doing/` with `Status: Doing` when Phase 1 starts.

## Success Criteria

1. A running import in the queue names the step its count measures, and shows how much of the run remains.
2. A Schedule Execution group header shows the Schedule's shape, including a failure inside a parallel step, without a hover.
3. Tasks with no phases render exactly as they do today.
4. The step number and step name reported by the portal, the REST API and PowerShell are identical for the same run at the same moment.
5. Status-to-appearance rules have exactly one definition, consumed by all three rails.
6. `dotnet build JIM.sln` and `dotnet test JIM.sln` clean; validated at runtime against the full stack, not only by tests.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| The extracted visuals helper drifts from the stepper's behaviour during Phase 1. | Phase 1 ships with no visual change and its own status-table test; the Activity page is compared before and after. |
| Two animations compete in the group header: the worker activity bar's shimmer sits directly above the rail. | Check on a real screen during runtime validation. If they fight, drop the rail's running-marker pulse and rely on colour, as the completed and waiting markers already do. |
| P3's wedge ordering is "tidied" into task order by a later change, silently making failures invisible at high fan-out. | The ordering rule gets its own named unit test and a comment at the implementation stating why. |
| CSS classes named in Razor but absent from `site.css` render as unstyled `<div>`s, invisible to build and to bUnit. | Grep `site.css` for every new `jim-` class after writing the markup, and confirm with `getComputedStyle` on the running stack (`JIM.Web/CLAUDE.md`). |
| Adding `.ThenInclude(a => a.Phases)` regresses queue load time. | Bounded by design: completed worker tasks are deleted, so the query only sees in-flight work. Confirm with the queue populated during runtime validation. |
| The in-memory provider auto-tracks navigation properties, hiding a missing `Include` in Phase 2. | A `RequiresPostgres` test for the header projection, per `test/CLAUDE.md`. |

## Benefits

- **UX:** the count on screen becomes interpretable, and a Schedule's progress and failures become visible from the queue rather than only after opening each Activity.
- **Architecture:** status-to-appearance rules and Schedule-step grouping each get one definition instead of one per surface, matching what `RunPhaseReading` already does for step numbering.
- **Consistency:** the queue becomes a compact view of the Activity page rather than a competing idea, and all three administrator surfaces report the same sentence.
