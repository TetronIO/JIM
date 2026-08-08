# Run Profile Phases

> Design note for the steps of a Run Profile execution: how JIM declares them, how a Connector contributes its own, and how they reach the portal, the REST API and PowerShell.

## Status: Implemented (2026-08-01)

Implemented under [#454](https://github.com/TetronIO/JIM/issues/454). Supersedes [`CONNECTOR_SUB_PHASE_PROGRESS.md`](CONNECTOR_SUB_PHASE_PROGRESS.md) (#637), whose narration-only mechanism this design absorbs and extends.

## Background

An Activity could say what a run was doing, but not where that sat in the run. Two consequences, both reported:

- The object counters reset between phases, so the progress bar refills from zero several times during one import. Nothing said how many more times that would happen, and `ActivityProgress` had to document the reset so consumers did not read it as lost work.
- #637 gave Connectors a way to narrate their internal work, which closed the "frozen message" gap but not the "where am I" one: the longest, most opaque stretch of a run (a file load, a paged directory fetch) still had no visible end.

The original issue proposed a single free-form `Activity.CurrentPhase` string, with the portal inferring the step position by matching the text against a known ordered list. That was designed before #637 shipped. Once Connector-owned vocabulary reaches the same channel, text matching fails exactly during the phases #637 exists to narrate, and third-party Connectors make the vocabulary open-ended. The design below replaces it.

## Design

### Declared up-front, not discovered

The point of a stepper is the steps you have not reached. That is only expressible if the steps are known before the work happens, so both JIM's phases and a Connector's are **declared at the start of the run** and recorded then:

- `RunProfilePhaseCatalogue` (JIM.Models) is the single declaration of JIM's own phases per run type. Adding or renaming a step is an entry here plus the `RunPhaseKeys` constant at the worker call site that enters it; `RunProfilePhaseCatalogueTests` fails if the two drift apart.
- `IConnectorPhases.GetPhases(connectedSystem, runProfile)` is the Connector's declaration, read once before the run. A Connector that declares nothing still works: it narrates into the JIM step that called it.

A declaration is an expectation, not a promise. A phase the run turns out not to need is recorded as **skipped** when a later phase is entered, so a Delta Import's deletion detection reads as "not needed" rather than sitting pending forever.

Skipping is for work the run *could* have done. Work a run is structurally incapable of is left out of the declaration entirely, via `ActivityPhaseSet.Declare`'s inapplicable-phase set: a file-based import opens no connection, so declaring one would put a permanently not-needed step in every file-based run's stepper, saying nothing. The reporter decides this, because it is the only place that knows both the run type and the Connector.

### Connector phases nest; they are not peers

A Connector's phases are recorded with `ParentKey` set to the JIM phase that calls it (the one flagged `HostsConnectorPhases` in the catalogue). Two reasons:

1. **Comparability.** "Step 3 of 7" means the same thing whichever Connector is configured. Peer-level Connector steps would make the same logical run read as a different length per Connector, and a chatty Connector would drown JIM's own journey.
2. **Focus.** The portal shows a step's Connector detail only while that step is running, so the administrator sees the detail that is live rather than every Connector step of the whole run.

### Transition rules

`ActivityPhaseSet` (JIM.Models) holds the run's phases in memory for the length of the run and owns the rules, free of any dependency on the database or the worker so they are provable in isolation:

- Entering a phase completes whatever else was running, **except** the phase hosting it: a Connector phase and the JIM phase around it are active together.
- Declared phases passed over are recorded as skipped.
- Re-entering a phase reopens it rather than duplicating it, so a paged import looping between fetching and parsing reads as two steps taking a while, not forty steps.
- A phase entered but never declared is **appended**, not dropped, so a Connector that narrates something unexpected still shows up. It cannot be shown in advance, which is the cost of not declaring it.
- Finishing the run completes what was running, or marks it failed when the run failed or was cancelled, and skips whatever was never reached.

Only the rows a transition actually changed are persisted, so narrating a run costs a handful of small writes.

### Where it is written and read

- **Write:** `ActivityPhaseReporter` (JIM.Worker) owns the lifetime. `Worker.cs` declares the phases before the run and closes them out in its `finally`, so the step a run failed in is recorded whatever happened. Every write is guarded: narration is cosmetic, and losing a step must never cost an administrator their run.
- **Persistence:** `ActivityPhase` rows, upserted through raw SQL for the same reason `UpdateActivityMessageAsync` is raw SQL (the worker's DbContext carries the run's tracked entities). Column lists are guarded by `BulkInsertColumnCompletenessTests` with a `RequiresPostgres` round trip.
- **Notification:** a phase transition writes no Activity column, so the #307 progress trigger would not fire for it. `ActivityPhases` publishes on the same channel with the same payload; PostgreSQL collapses the seed burst into one notification because it is one transaction.
- **Read:** the progress read carries the phases (a handful of rows) so the portal, the API and PowerShell make one call, not two.

### The three consumers, and what stops them drifting (#1162)

The Activity page was the first consumer. Two more arrived with the Operations queue, and each new reader is a chance for "what counts as a step" or "what does a skipped step look like" to acquire a second definition. Three pieces exist to stop that:

- **`RunPhaseReading` (JIM.Models)** answers "which of these rows are steps of the run, and which one is running". A Connector's phase nests under its host and is never a top-level step, so filtering `ParentKey == null` at a call site is the thing that must not happen; it would make the same Run Profile read as a different length depending on which Connected System it ran against.
- **`RunPhaseSummary` (JIM.Models)** is that reading reduced to what a list view needs: the current step's name and position, the total, and each step's status. It rides on `WorkerTaskHeader`, so the queue, the Worker Tasks REST read and `Get-JIMWorkerTask` all describe a run from one projection rather than three.
- **`RunPhaseVisuals` (JIM.Web)** is the appearance-side sibling: status to CSS modifier, status to icon, and the fill a step's bar should show. `RunPhaseStepper` (the Activity's labelled rail), `RunProgressMetrics` and `RunPhaseMicroRail` (the queue cell's few-pixel segments) all consume it. Before it existed there were two copies of the status-to-icon mapping, and they had already diverged.

The sentence itself ("Step 3 of 7: Saving changes") is composed in three languages: `WorkerTaskProgress.razor` and `RunProgressMetrics.razor` in the portal, `ScheduleStepReading` for the Schedule-level equivalent, and `Get-JIMStepPositionDisplay` in the PowerShell module. They are pinned by tests on each side rather than shared, because sharing a format string across a Razor component and a PowerShell function is not possible; a change to the wording has to be made in all of them.

**One level up, `ScheduleStepReading` (JIM.Models) does the same job for a Schedule Execution's steps.** It is not built on phases: a Schedule step is a whole Run Profile execution, and its evidence lives half in `WorkerTask` rows and half in `Activity` rows, because a task is deleted the moment its work finishes. Read the class's own remarks before assuming the two models are parallel.

### Why the message stayed where it is

The issue's comment proposed splitting live progress text out of `Activity.Message` into its own field. That is no longer needed: the portal shows the message under the step it describes, and the Summary panel shows it only once the run is no longer in progress, so the double-print the comment described is gone without a schema change or an API break. `Message` keeps its two roles, but they are now visually separated by the thing that distinguishes them: whether a step is running.

## Deviations from the issue's original design

- **Option 1 (free-form `CurrentPhase` string, UI infers position) was rejected**, for the text-matching reason above.
- **Option 3 (phase list on the Activity) is what shipped**, because it is the only one that delivers per-step durations after a page refresh, and on Activities opened days later.
- **MudStepper is used, horizontally, with all three of its templates.** `LabelTemplate` draws the step marker (an icon for the work that step does), `TitleTemplate` its label and duration, and `ConnectorTemplate` the line between two steps, which is what makes the rail a stepped progress bar: each leg belongs to the step it leaves and fills with that step's own progress. Its wizard behaviour is suppressed (`ActionContent` empty, `OnPreviewInteraction` cancels every click), and its fixed 175px step basis is overridden, because at seven steps it squeezed the connectors to nothing. A Connector's own steps and the live message sit beneath the rail, which is the one thing a horizontal layout cannot hold inline.

## Per-Connector phase catalogue

### FileConnector

| Run type | Steps |
|---|---|
| Export | `load-existing-file` (Loading existing export file), `merge` (Merging changes into file), `write` (Writing the output file) |
| Full and Delta Import | `read` (Reading the file) |

One import step, because reading and parsing are one pass over the file; the rolling row count is narration within it, not a step of its own.

### LdapConnector

| Run type | Steps |
|---|---|
| Full Import | `root-dse` (Querying the directory), `fetch` (Fetching objects) |
| Delta Import | `root-dse`, `query-changes` (Querying changes), `fetch`, `query-deletions` (Querying deleted objects) |
| Export | none |

Export declares nothing: it iterates per object and JIM already reports accurate per-item counts, so a step would say less than the counts do. The interface is still there if that ever changes.

## Governance

Enforcement, in preference to documentation nobody rereads:

- `RunProfilePhaseCatalogueTests` guards JIM's own declaration: unique keys, one Connector host phase per run type, names in house style, and every `RunPhaseKeys` constant declared somewhere.
- `ConnectorPhaseConformanceTests` is a base class every phase-declaring Connector's tests derive from: deterministic declaration, unique keys, keys that are identifiers and names that are labels, no declaration for synchronisation run types, and no throwing on a run type the Connector does not support.
- `ActivityPhaseSetTests` covers every transition rule, including the awkward ones (nesting, looping, undeclared keys, failure).
- Public guidance for Connector authors lives in [`docs/developer/connectors.md`](../../docs/developer/connectors.md); the administrator's view is in [`docs/configuration/activities.md`](../../docs/configuration/activities.md).

## Possible future work

- **Clicking a step to filter that step's execution items or logs.** Out of scope for #454, and the data model supports it without change.
- **Per-step throughput.** `ActivityEtaTracker` currently treats a counter reset as a proxy for a phase change; with a real phase identity it could key its sample window off the step instead, and report a rate per step rather than per run.
- **Retroactive phases** for Activities that ran before this shipped. Deliberately not attempted: the timings were never recorded, and inventing them would be worse than showing nothing.
