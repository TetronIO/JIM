# Schedule Failure Handling

- **Status:** Planned
- **Created:** 2026-09-23
- **Author:** JayVDZ (PRD drafted via Claude Code)
- **Issue:** [#1787](https://github.com/TetronIO/JIM/issues/1787)

## Problem Statement

Administrators already decide, per step, whether a failed step stops the Schedule: the **Continue on failure** setting, which defaults to stopping. Three gaps remain:

1. **A run that carried on past a failed step reports success.** The Schedule Execution ends as a green "Complete", so the failure is visible only by reading the step list. Monitoring that reads `LastExecutionStatus` sees a clean run.
2. **Nobody can see a Schedule's failure behaviour at a glance.** The step list in the Schedule editor does not show which steps stop and which continue; each step has to be opened.
3. **Setting a whole Schedule to carry on is tedious and fragile.** Many administrators want a Schedule to keep going through failures, the way traditional ILM schedules are commonly configured. Today that means switching the setting on for every step, and remembering to do so for every step added later.

## Goals

- A run that continued past one or more failed steps is distinguishable from a clean run, in the portal, the REST API and PowerShell.
- Administrators set failure behaviour once per Schedule and make exceptions per step.
- The step list shows each step's effective behaviour and where it comes from.
- After the upgrade, every existing Schedule behaves exactly as before until an administrator changes it.

## Non-Goals

- Changing what counts as a step failure. A step fails when its Activity ends Complete With Error, Failed With Error or Cancelled; warnings, including object-level data errors, still do not count.
- Changing the default. New Schedules stop when a step fails.
- An execution-level warning status for steps that completed with warnings.
- Retry policies, or notifications and alerting on failure.
- Start-failure handling and the parallel-group rule. Both are delivered by #1768 and are relied on here.

## User Stories

1. As an administrator, I want to set a whole Schedule to continue when a step fails, so that I don't have to configure every step, including ones added later.
2. As an administrator, I want one step to stop the Schedule even when the Schedule continues on failure, so that a step whose failure makes later steps unsafe can still halt the run.
3. As an administrator, I want to see which steps will stop or continue on failure, and why, without opening each step.
4. As an operator running a monitoring script, I want a run that continued past a failure reported differently from a clean run, so that I can alert on it.

## Requirements

### Functional Requirements

1. **Schedule setting.** Each Schedule has a "When a step fails" setting: **Stop the Schedule** (default) or **Continue the Schedule**.
   - Portal: on the Schedule editor's Steps tab, above the step list.
   - REST API: readable and writable on the Schedule DTOs (create, update, read).
   - PowerShell: a parameter on `New-JIMSchedule` and `Set-JIMSchedule`; included in `Get-JIMSchedule` output.
2. **Step setting becomes three-way.** Each step's setting is **Follow the Schedule** (default for new steps), **Stop the Schedule** or **Continue the Schedule**.
   - Portal: replaces the on/off switch in the step editor. The Follow option states the Schedule's current value, for example "Follow the Schedule (currently: continue)".
   - REST API: a new step property (for example `onFailure`: `FollowSchedule` | `Stop` | `Continue`). The existing `continueOnFailure` remains, reporting the effective behaviour on read. On write, when `onFailure` is absent, `continueOnFailure: true` maps to `Continue` and `false` to `FollowSchedule`. Existing clients always send the field, and `false` has always meant "the default", so mapping it to `Stop` would pin every step created by an existing script and defeat the Schedule setting.
   - PowerShell: `Add-JIMScheduleStep` gains `-OnFailure FollowSchedule|Stop|Continue` (default `FollowSchedule`). `-ContinueOnFailure` remains as shorthand for `-OnFailure Continue`; supplying both is an error.
3. **Effective behaviour.** A step's effective behaviour is its own setting, or the Schedule's when it follows the Schedule. Every decision the Scheduler makes about a failed step uses the effective behaviour, read at the moment of the decision (as today): Worker-side advancement, the recovery sweep, start-failure handling and the parallel-group rule from #1768. Resolve it in one place, not per caller.
4. **Upgrade.** A migration maps existing steps with Continue on failure off to **Follow the Schedule**, and on to **Continue the Schedule**, and sets every existing Schedule to **Stop the Schedule**. Net behaviour is unchanged.
5. **Step list.** In the Schedule editor, every step, including each step of a parallel group, shows its effective behaviour and its source, for example "Continues on failure · From the Schedule" or "Stops on failure · Set on this step".
6. **"Complete With Error" execution status.** A new `ScheduleExecutionStatus` member, `CompleteWithError`, displayed as "Complete With Error" in the Tertiary colour to match an Activity's Complete With Error. It applies to an execution that reached its end with at least one step that failed and was allowed to continue.
   - It counts as finished everywhere Complete does: not active; eligible as the last-run outcome on the Schedules list; reported by `LastExecutionStatus`; available in execution history filters.
   - The REST enum is a published contract serialised by name, so this is an additive member. Document it in the API and PowerShell references.
7. **Explaining a continued failure.** When an execution is Complete With Error, the Schedule Execution page shows a warning alert naming each failed step, for example: "The Schedule finished, but step 3, Active Directory / Export, failed. It is set to let the Schedule continue."
8. **Schedules list.** The last-run outcome shows Complete With Error distinctly and names the failed step, as it does for failures today.
9. **Change history.** Changes to the Schedule setting and to each step's setting appear in the Schedule's configuration change history, alongside the existing "Continue on failure" entry.

### Non-Functional Requirements

- Surface parity: the portal, REST API and PowerShell all ship in the same PR, each with tests and docs.
- No new third-party dependencies.

## Examples and Scenarios

The example Schedule, "Nightly HR sync", has four steps: 1 HR / Full Import, 2 HR / Full Synchronisation, 3 Active Directory / Export, 4 Service Desk / Export.

### Scenario 1: The Schedule continues, and a step fails

**Given**: the Schedule is set to Continue the Schedule, and every step follows the Schedule
**When**: step 3 fails (the domain controller refuses the connection)
**Then**: step 4 still runs; the execution ends **Complete With Error**; the page shows a warning alert naming step 3; `Get-JIMSchedule` reports `LastExecutionStatus` as `CompleteWithError`

### Scenario 2: A step overrides the Schedule to stop

**Given**: the Schedule is set to Continue the Schedule, and step 3 is set to Stop the Schedule
**When**: step 3 fails
**Then**: step 4 is not run (its Activity reads "Not run: an earlier step stopped the Schedule."), and the execution ends **Failed**

### Scenario 3: A step overrides the Schedule to continue

**Given**: the Schedule is set to Stop the Schedule (the default), and step 3 is set to Continue the Schedule
**When**: step 3 fails
**Then**: step 4 runs, and the execution ends **Complete With Error**, not Complete

### Scenario 4: Upgrade

**Given**: an existing Schedule whose step 2 has Continue on failure on and whose other steps have it off
**When**: JIM is upgraded
**Then**: the Schedule is set to Stop the Schedule, step 2 is set to Continue the Schedule, the other steps follow the Schedule, and every run behaves as it did before

### Scenario 5: An existing script adds a step

**Given**: a script that calls `Add-JIMScheduleStep` without `-ContinueOnFailure`, against a Schedule set to Continue the Schedule
**When**: the script runs
**Then**: the new step follows the Schedule, so it continues on failure

## Constraints

- British English throughout. Title Case JIM entity names (Schedule, Schedule Execution, Activity).
- Never name competing products; say "traditional ILM solutions".
- The `ScheduleExecutionStatus` REST contract is additive only; no existing member is renamed or removed.

## Affected Areas

| Area | Impact |
|------|--------|
| Database | New Schedule-level failure behaviour column; the step's `ContinueOnFailure` boolean replaced by a three-way value, with a data-mapping migration; new `ScheduleExecutionStatus` member |
| Application | One effective-behaviour resolver used by advancement, the sweep and start-failure handling; completion sets Complete With Error; configuration snapshot rendering |
| API | Schedule and step request and response DTOs; execution status enum; regenerated OpenAPI document |
| PowerShell | `New-JIMSchedule`, `Set-JIMSchedule`, `Get-JIMSchedule`, `Add-JIMScheduleStep`, `Get-JIMScheduleExecution` (output shape), with Pester tests |
| UI | Schedule editor (Schedule setting, three-way step setting, step list indicators); Schedule Execution page (status, alert); Schedules list outcome; status colours in `Helpers.cs` |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/configuration/schedules.md` | Rewrite "Continue on failure" as the Schedule setting plus per-step exceptions; describe Complete With Error |
| `docs/powershell/schedules.md` | New parameters and output fields |
| `docs/developer/diagrams/SCHEDULE_EXECUTION_LIFECYCLE.md` | Complete With Error as a terminal state; effective-behaviour resolution |

## Dependencies

- #1768 lands first. It adds start-failure handling that honours Continue on failure, and the parallel-group rule (the failing step's own setting decides); this feature swaps the per-step setting in both for the effective behaviour.

## Open Questions

1. **Built-in Schedules.** Should their Schedule setting be editable? Recommendation: no; JIM manages their steps and behaviour.
2. **Editing one step from PowerShell.** Today an existing step's setting can only be changed by resending the whole step list through `Set-JIMSchedule -Steps`. Add a `Set-JIMScheduleStep` cmdlet? Recommendation: yes, if the implementation plan shows it is small; otherwise document the `-Steps` route.
3. **Names.** Final names for the REST properties and enum values, settled in the implementation plan against existing DTO conventions.

## Acceptance Criteria

- [ ] An administrator can set a Schedule to continue on failure in the portal, the REST API and PowerShell.
- [ ] Each step can follow the Schedule, stop or continue, in all three surfaces; new steps follow the Schedule.
- [ ] Every Scheduler decision about a failed step uses the effective behaviour, including start failures and parallel groups.
- [ ] After the upgrade, existing Schedules behave exactly as before (covered by a migration test).
- [ ] The step list shows each step's effective behaviour and its source.
- [ ] A run that continued past a failed step ends Complete With Error, shows the explaining alert, and is reported as `CompleteWithError` by the API and PowerShell.
- [ ] Docs and change history cover the new settings and status.

## Additional Context

- #1768: safe Schedule starts (option B), which this builds on.
- #1765: advancing a Schedule's next run time when its start fails.
- The design explainer with portal mocks of these scenarios was reviewed with the repository owner on 2026-09-23; decisions 1, 3 and 6 there are this PRD.
