# Run Profile Safeguards

- **Status:** Doing
- **Created:** 2026-09-03
- **Author:** Jay Van der Zant
- **Issue:** [#1618](https://github.com/TetronIO/JIM/issues/1618)

## Problem Statement

A broken import filter or base DN, a mistaken Synchronisation Rule change, or a Connector Space clear followed by a partial re-import can each turn one run into a mass change: thousands of exports written to a target system, or thousands of Connected System Objects marked as deleted and their Metaverse Objects put through their Deletion Rules. Nothing in JIM bounds the size of a single run. The only defences are Deletion Rule grace periods and an administrator reading the Activity afterwards, by which time the exports have been written.

Traditional ILM solutions carry a delete threshold on their export step for exactly this reason. JIM should carry that, extend it to creates and updates, and add the equivalent on the import side. #1605's Full Import gate closes the "synchronise an empty Connector Space" trap, but cannot cover a Full Import that succeeds while returning far fewer objects than before: the objects it did not see are marked as deleted, and the next synchronisation disconnects them all.

## Goals

- An administrator can bound, per Run Profile, how many creates, updates and deletes an export run may attempt, and how many deletions a Full Import may detect, as a count and as a share of the Connector Space.
- A run that reaches a limit stops that kind of change, completes with a warning that states the limit, the count reached and what remains, and leaves everything it did not do exactly where it was, so the next run picks it up without further action.
- A Full Import that trips its deletion limit marks nothing as deleted and does not open the post-clear reconciliation gate (#1605).
- Every limit and every outcome is available on all three surfaces: portal, REST API, PowerShell.

## Non-Goals

- Global or per-Connected-System defaults for the limits. Every limit is null (no limit) on upgrade and on new Run Profiles; the docs recommend values. See Decision 1.
- Applying the deletion limit to Delta Import. Delta deletes are reported explicitly by the connector and applied page by page as they arrive; holding them back until the end of the run would need buffering this PRD does not ask for. A follow-on issue if wanted.
- Marking withheld Pending Exports. They stay Pending and unchanged, indistinguishable from exports queued after the run. A Pending Exports page filter is a possible follow-on.
- Changing what Preview mode reports. Preview lists what would be processed; the limits act at execution.
- Pausing synchronisation (#1619).

## User Stories

1. As an administrator, I want an Export Run Profile to stop deleting after a number of deletes I choose, so that a broken source cannot deprovision my directory in one scheduled run.
2. As an administrator, I want a Full Import to refuse to mark more than a share of the Connector Space as deleted, so that a wrong filter or base DN cannot start thousands of Deletion Rules.
3. As an administrator, I want a capped run to be a warning rather than a failure, so that my Schedule carries on and I see the backlog on the Activity and on the Connected System.
4. As an administrator who scripts JIM, I want to set and clear these limits from PowerShell and the REST API, and read the withheld counts from the Activity.

## Requirements

### Functional Requirements

1. **Run Profile fields.** A Run Profile carries five optional whole numbers: Max creates, Max updates and Max deletes (Export run type only); Max detected deletions and Max detected deletions percent (Full Import only; percent is 0 to 100). Null means no limit. Zero is a valid limit ("attempt none of these"). Setting an export limit on a Run Profile that is not an Export, or a detection limit on one that is not a Full Import, is rejected on every surface with a message naming the field, mirroring the existing Verification Mode rule.
2. **Export counting.** During an Export run, each Pending Export attempted against the connector counts once against its change type's limit, whether it succeeds or fails, and whether it is written in the first pass or the deferred-reference pass. An export withheld because its type's limit has been reached is not attempted, not marked, not failed and not given an execution item; its status stays Pending. Other change types continue to their own limits. Counting is in memory and correct under parallel export batches.
3. **Export warning.** When any limit was reached, the Activity completes as Complete with warning (unless a stronger outcome applies), and its warning carries one sentence per capped type, for example: "Stopped processing deletes after 100, this Run Profile's limit; 342 deletes remain pending." The completion message counts attempted work as it does today. The progress counters end at the attempted plus withheld total, so the run reads as finished.
4. **Export counters.** The Activity records how many exports of each type were withheld (three counters). They are populated, zero when nothing was withheld, on every Export run, and null on every other Activity.
5. **Resumption.** The next Export run of the Connected System processes the withheld exports in the ordinary order. Nothing needs resetting. Raising or clearing the limit, or fixing the cause, is the whole remedy.
6. **Full Import detection.** Deletion detection first resolves which Connected System Objects in the run's scope would newly be marked as deleted (excluding objects processed by this run and objects already marked), then compares that count with the limits. The detection is refused if the count exceeds Max detected deletions, or if count × 100 exceeds Max detected deletions percent × the number of Connected System Objects in the run's scope at the start of the run. If refused, no object is marked as deleted. Objects the import did see are still created and updated as normal. The existing refusal when the import returned zero objects is unchanged.
7. **Full Import warning.** A refused detection completes the Activity as Complete with warning, with a message of the form: "Deletion detection found 4,120 objects (41% of 10,000) no longer in the Connected System, above this Run Profile's limit of 10%; none were marked as deleted. Check the Connected System's scope and the connector's filters, or raise the limit, then run the Full Import again." The Activity records the withheld count (one counter; null on every other Activity).
8. **Gate interaction.** A Full Import that refused its deletion detection is not a successful Full Import for the post-clear reconciliation gate: it does not stamp the Connected System's last successful Full Import time, so the reconciliation sweep stays shut until an import passes.
9. **Schedules.** A capped or refused run is a warning outcome. Schedules treat it exactly as they treat any Complete with warning today.
10. **Portal.** The Create and Edit Run Profile dialogs show a Safeguards group whose fields depend on the run type. The Run Profiles tab shows a chip per configured limit beside the run type. The Activity detail page shows a Safeguards panel with the withheld counts when any is above zero, beside the existing warning. The Connected System page shows a notice when the most recent completed Export run withheld changes, naming the Run Profile and the limit and linking to the Activity.
11. **REST.** `RunProfileDto` exposes a `safeguards` object carrying the five values. Create accepts an optional `safeguards` object. Update accepts an optional `safeguards` object which, when present, replaces all five values (a null member clears that limit; an absent object leaves all five unchanged). `ActivityDetailDto` exposes the four withheld counters. The OpenAPI document is regenerated.
12. **PowerShell.** `New-JIMRunProfile` and `Set-JIMRunProfile` gain `-MaxCreates`, `-MaxUpdates`, `-MaxDeletes`, `-MaxDetectedDeletions` and `-MaxDetectedDeletionsPercent`, each a nullable integer. On Set, an explicit `$null` clears the limit and an omitted parameter leaves it unchanged. `Get-JIMRunProfile` output carries `safeguards`; `Get-JIMActivity` output carries the four counters.
13. **Docs and changelog.** A Safeguards section in the Run Profiles docs with recommended values; the PowerShell Run Profile and Activity references; a changelog entry.
14. **Shared maths.** The share comparison uses long arithmetic and one helper shared with #1605's re-join shortfall check, so the two thresholds cannot drift apart.

### Non-Functional Requirements

- No per-export database query for the limit: the ledger is in memory and thread-safe.
- The two-phase deletion detection performs no more database work than today's single pass; it changes the order of the same lookups.
- Migration adds nullable columns only; no backfill, no data rewrite.
- No new NuGet packages.

## Examples and Scenarios

### Scenario 1: Max deletes reached on export

**Given**: an Export Run Profile with Max deletes 100 and no other limit; 442 delete, 1,200 update and 12 create Pending Exports.
**When**: the Run Profile runs.
**Then**: 100 deletes, 1,200 updates and 12 creates are attempted; 342 deletes remain Pending and untouched; the Activity is Complete with warning, its warning reads "Stopped processing deletes after 100, this Run Profile's limit; 342 deletes remain pending.", and its counters show 342 deletes withheld and zero of the other two. The next Export run attempts the 342.

### Scenario 2: A limit of zero

**Given**: an Export Run Profile with Max deletes 0.
**When**: the Run Profile runs with deletes pending.
**Then**: no delete is attempted; creates and updates proceed; the warning names the zero limit and the number remaining.

### Scenario 3: Full Import trips the share limit

**Given**: a Full Import Run Profile with Max detected deletions percent 10; 10,000 Connected System Objects in scope; the connector, with a broken filter, returns 5,880 of them.
**When**: the Run Profile runs.
**Then**: the 5,880 are updated as normal; none of the 4,120 missing objects is marked as deleted; the Activity is Complete with warning with the message in FR7; the withheld counter is 4,120; the Connected System's last successful Full Import time is not stamped. The administrator fixes the filter and runs the Full Import again, which passes and marks the genuinely departed objects.

### Scenario 4: Full Import within both limits

**Given**: Max detected deletions 500 and Max detected deletions percent 10; 10,000 objects; 300 missing.
**When**: the Run Profile runs.
**Then**: 300 objects are marked as deleted exactly as today; the Activity is Complete; the withheld counter is 0.

### Scenario 5: Clearing a limit from PowerShell

```powershell
Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 12 -MaxDeletes $null
```

**Then**: the delete limit is removed; the other four values are unchanged; the API receives the full `safeguards` object with `maxDeletes: null`.

### Scenario 6: Wrong run type

**When**: `New-JIMRunProfile -RunType DeltaImport -MaxDeletes 10` is called, or the REST create carries `safeguards.maxDeletes` on a DeltaImport.
**Then**: the request is rejected with "MaxDeletes can only be set on an Export Run Profile."

## Constraints

- British English throughout; "Run Profile", "Pending Export", "Connector Space" and "Full Import" are proper nouns in UI text and docs.
- Air-gapped and self-contained; nothing here reaches outside the process.
- The wire shape for the limits is one nested object, so the update contract can distinguish "clear this limit" from "leave it alone". The flat nullable-integer precedent (Max Export Parallelism) has no way to clear a value and is not copied.

## Affected Areas

| Area | Impact |
|------|--------|
| Database | Five nullable integer columns on `ConnectedSystemRunProfiles`; four nullable integer columns on `Activities`; one migration per stack layer |
| Models | `ConnectedSystemRunProfile`, `Activity`, `ExportExecutionOptions`, `ExportExecutionResult`; a thread-safe export limit ledger; a shared share-comparison helper |
| Application | `ExportExecutionServer` batch loop and deferred pass honour the ledger; Run Profile validation in `ConnectedSystemServer`; the #1605 shortfall check reuses the helper |
| Worker | `SyncExportTaskProcessor` warning and counters; `SyncImportTaskProcessor` two-phase deletion detection; `FullImportSuccessEvaluator` takes the withheld count; `Worker.cs` passes it |
| API | `RunProfileDto`, `CreateRunProfileRequest`, `UpdateRunProfileRequest`, a `RunProfileSafeguardsDto`; `ActivityDetailDto`; controller validation |
| PowerShell | `New-JIMRunProfile`, `Set-JIMRunProfile`; Pester tests |
| UI | `ConnectedSystemRunProfilesTab.razor` dialogs and chips; `ActivityDetail.razor` Safeguards panel; `ConnectedSystemDetail.razor` notice |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/configuration/run-profiles.md` | New "Safeguards" section: the five limits, what a capped run does, recommended values, the Full Import gate interaction |
| `docs/configuration/activities.md` | Withheld counts and the warning shape |
| `docs/powershell/run-profiles.md` | Parameters, output shape, clearing example |
| `docs/powershell/activities.md` | The four counters in the output table |
| `docs/concepts/synchronisation-pipeline.md` | One paragraph under Full Import deletion detection and under Export |
| `CHANGELOG.md` | ✨ entry under `[Unreleased]` |

## Dependencies

- #1605 (merged): the Full Import success predicate and the shortfall share comparison this feature extends.

## Decisions

The issue left three choices to the PRD. Recommended answers, each overridable before implementation starts:

1. **Defaults on upgrade and on new Run Profiles: none.** A new Connected System's first export is legitimately a mass create, and its first Full Import after a scope change legitimately detects many deletions; a seeded default would make both look broken to a new administrator, and a limit that is routinely raised to get past initial load is a limit nobody trusts later. The docs recommend values instead. Trade-off: an existing deployment gains no protection until an administrator sets a limit.
2. **Deletion limit as both a count and a share.** The share catches the catastrophic case at any scale; the count is what a small Connector Space needs, where one genuine departure is a large share. Either can be left blank, and whichever trips first refuses.
3. **Surface a withheld backlog on the Connected System page.** One notice, derived from the most recent completed Export Activity of the system, so a backlog of deliberately unprocessed deletes cannot sit unnoticed behind a warning-status Activity on a busy Operations page. One query per page load.

## Acceptance Criteria

- [ ] The five limits can be set, read and cleared on all three surfaces, with the run-type validation in FR1.
- [ ] An Export run honours each limit per change type, counts attempts in both passes, leaves withheld exports Pending and untouched, and completes with the warning and counters in FR3 and FR4. Boundary tests: limit of 0, limit equal to the queue, limit below the queue, no limit.
- [ ] A Full Import refuses its deletion detection when either limit is exceeded, marks nothing, updates the objects it saw, completes with the warning and counter in FR7, and does not stamp the last successful Full Import time. Boundary tests: at the limit, one above, one below, for both count and share.
- [ ] The next run resumes withheld exports without any reset.
- [ ] The Activity detail page, Run Profiles tab, dialogs and Connected System notice render as in the mock.
- [ ] Docs and changelog updated; OpenAPI regenerated; Pester tests for the new parameters.
- [ ] Runtime verification through the integration runner: one scenario test for the export limit and one for the Full Import limit.

## Additional Context

- UI mock: [Run Profile Safeguards artefact](https://claude.ai/code/artifact/ca8447ec-3b68-4372-9e7e-efed62744fcb).
- #1605's After the Clear artefact, for the gate this feature respects.
