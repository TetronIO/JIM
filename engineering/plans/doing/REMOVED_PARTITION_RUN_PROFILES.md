# Run Profiles that Target a Removed Partition

- **Status:** Doing (Phases 1-5 complete)
- **Issue:** [#2740](https://github.com/TetronIO/JIM/issues/2740)
- **Created:** 2026-11-04

## Overview

A hierarchy refresh (`ConnectedSystemServer.ImportConnectedSystemHierarchyAsync`) removes the Partitions the directory no longer has. Since #2753 it loads the Connected System change-tracked, Run Profiles included, and when it deletes a Partition EF Core clears `ConnectedSystemRunProfile.PartitionId` on the client for every Run Profile that targets it (the foreign key is `ClientSetNull`). A Partition-scoped Run Profile silently becomes one that reads every selected Partition, including a Full Import whose deletion detection then covers the whole Connected System. Nothing records it.

This plan gives Run Profiles an enabled state, has the refresh disable the Run Profiles it would otherwise widen (or apply nothing, if the administrator prefers), and makes every way of starting a Run Profile refuse a disabled one.

## Business Value

- **No silent widening.** A Run Profile never starts reading scope the administrator did not give it.
- **No silent mass deletion.** A Full Import scoped to one Partition cannot turn into a whole-system deletion detection pass because a refresh ran.
- **Administrators can park a Run Profile.** Disabling a Run Profile is useful in its own right (maintenance windows, a Schedule step to skip for a while), without deleting it or editing every Schedule.

## Decisions (made by the user; not to be revisited)

1. **When a refresh would remove a Partition that Run Profiles target, the administrator chooses:** **Disable** those Run Profiles (the default), or **Cancel** (apply nothing from this refresh). There is no Delete option. Nothing from the refresh is saved until the choice is made.
2. **Run Profiles get an enabled state**, added with a migration in which existing rows are enabled.
   - Why a Run Profile was disabled is recorded, so the surfaces can say why. One disabled by a refresh records the name of the Partition that was removed.
   - Disabling through a refresh also clears its `PartitionId`, because the Partition is gone.
   - Administrators can enable and disable any Run Profile by hand, in the portal, the REST API and PowerShell.
3. **Scripts and the API default to Disable.** A refresh through REST or PowerShell that names no choice disables the affected Run Profiles, and the response and the refresh's Activity name each one. Callers can pass Cancel instead.
4. **Re-enabling a Run Profile that a removed Partition disabled needs a scope in the same change:** a Partition, or an explicit "all selected partitions". It must never silently widen. In REST, an enable request without either is refused with 400 and a message naming the remedy; the same holds in PowerShell and in the portal's edit dialog. A Run Profile disabled by hand simply re-enables.
5. **A Schedule step that runs a disabled Run Profile fails, with the reason,** the way a step whose Run Profile is missing behaves.

## Technical Architecture

### Model (`JIM.Models`)

- `ConnectedSystemRunProfile.Enabled` (`bool`, default `true`; database default `true` so existing rows are enabled).
- `ConnectedSystemRunProfile.DisabledReason` (`ConnectedSystemRunProfileDisabledReason?`): `Manual` (an administrator disabled it) or `PartitionRemoved` (a hierarchy refresh removed the Partition it targeted). Null while enabled. Unlike `SyncRule.DisabledReason` (free text, null when disabled by hand) this is an enum, because the re-enabling rule depends on it and must not parse a sentence.
- `ConnectedSystemRunProfile.DisabledPartitionName` (`string?`): the removed Partition's name, for `PartitionRemoved`.
- `RunProfileEnablement` (static, `JIM.Models.Staging`): the one wording of why a Run Profile is disabled and why a run of it is refused, shared by the queue, the Worker, the portal and the API; and the re-enabling rule as a pure function.
- `RemovedPartitionRunProfileAction` enum: `Disable`, `Cancel`.
- `HierarchyRefreshResult` gains `Applied` (default `true`) and `AffectedRunProfiles` (`HierarchyRefreshRunProfileItem`: id, name, the Partition it targeted, and whether this refresh disabled it).

### The refresh

`ImportConnectedSystemHierarchyAsync` gains a `RemovedPartitionRunProfileAction runProfileAction = Disable` parameter on both initiator overloads. After the merge and before the save, the core finds every Run Profile whose `Partition` is one of `removedPartitions` (the set the core already computes, compared by reference on the tracked graph).

- **Disable:** each is set `Enabled = false`, `DisabledReason = PartitionRemoved`, `DisabledPartitionName = <the Partition's name>`, `Partition = null`, audited as updated by the refresh's initiator, and saved with the hierarchy in the same `SaveChanges`. The Activity completes with a warning naming each Run Profile and the Partition it targeted.
- **Cancel, with any affected:** the change tracker is cleared (the merge lives only in tracked memory, and the Activity's own save would otherwise write it), nothing is saved, and the Activity completes with a warning naming the Partitions and the Run Profiles and saying that nothing was changed. The result carries `Applied = false`.
- **Either, with none affected:** the refresh applies exactly as before.

A Run Profile that was already disabled by hand and targets a removed Partition is affected too: its Partition is gone either way, and recording `PartitionRemoved` is what makes re-enabling it ask for a scope.

### Enforcement

- **Queue:** `TaskingServer.CreateWorkerTaskAsync` is the single choke point every run is queued through (portal, REST, PowerShell, Schedules). A disabled Run Profile is refused there, before partition validation, with `RunProfileEnablement.DescribeRunRefusal`.
- **Schedules:** a Schedule queues each step through that choke point when it starts; a refusal is already recorded as a Failed Activity ("Could not be queued: ...") and Continue On Failure applies, exactly as for a step whose Connected System is being deleted or whose Run Profile is missing. No Scheduler change is needed beyond a test.
- **Worker:** a task queued before its Run Profile was disabled (a Schedule's later steps are queued when the Schedule starts) is refused when the Worker picks it up: its Activity fails with the same reason before any run step is declared, and the task completes so the Schedule moves on under its failure rules.

### Updating a Run Profile

`UpdateConnectedSystemRunProfileAsync` (both initiator overloads) gains `bool allSelectedPartitions = false`: the caller's explicit statement that the Run Profile reads every selected Partition (it also clears any Partition, which REST could not do before). The server reads the stored enabled state with a projection (never trusting the caller's copy, and never a tracked instance that would already carry the caller's edit), then:

- enabled: the reason and Partition name are cleared;
- newly disabled: the reason is `Manual`;
- still disabled: the stored reason and Partition name are kept;
- re-enabling a `PartitionRemoved` Run Profile with no Partition and no `allSelectedPartitions` is refused (`ArgumentException`, 400 in REST) with a message naming the remedy.

The configuration snapshot records a disabled Run Profile's state (by presence, as the Full Import switches are), so the change shows in the Connected System's change history.

### REST

- `RunProfileDto` gains `enabled`, `disabledReason`, `disabledPartitionName`.
- `UpdateRunProfileRequest` gains `enabled` (null leaves it) and `allSelectedPartitions` (mutually exclusive with `partitionId`).
- `POST .../import-hierarchy` takes an optional body, `ImportConnectedSystemHierarchyRequest { runProfileAction: Disable | Cancel }`, as `import-schema` takes its optional `disableDependents`/`removeDependents` body.
- **A refused refresh returns 409 Conflict** with a `HierarchyRefreshRefusedResponse` (`message`, `affectedRunProfiles`). The controller already returns 409 with a typed body for a change refused before anything was saved (the configuration test save gate's `ConfigurationTestGateResponse`, and `import-schema`'s Apply and Remove refusal), and PowerShell already turns such a body into a readable error naming each item and the way forward. An applied refresh returns 200 with `applied`, and `affectedRunProfiles` naming each Run Profile it disabled.
- `POST .../execute` refuses a disabled Run Profile with 400, the shape every queue refusal already takes.

### PowerShell

- `Get-JIMRunProfile`: the new DTO properties appear in its generated Output table; an example lists disabled Run Profiles.
- `Set-JIMRunProfile`: `-Enabled [bool]` and `-AllSelectedPartitions [switch]`.
- `Import-JIMConnectedSystemHierarchy`: `-RunProfileAction Disable | Cancel` (default `Disable`), and a warning naming each Run Profile the refresh disabled.
- `Invoke-JIMApi`: a 409 body carrying `affectedRunProfiles` is expanded into the error, with the way to go ahead.
- `Start-JIMRunProfile`: the API's refusal is surfaced as it is.

### Portal

- **Scope tab (`ConnectedSystemPartitionsTab`):** Retrieve Hierarchy calls with Cancel. If the result was not applied, `RemovedPartitionRunProfilesDialog` lists each Run Profile and the Partition it targets; **Disable Run Profiles** (filled primary) calls again with Disable, **Cancel** (text) does nothing more. The unsaved-edits confirmation (#2743) still runs first.
- **Run Profiles tab:** a Disabled chip with the reason as its tooltip; the edit dialog has an Enabled switch, and re-enabling a Run Profile a removed Partition disabled replaces the optional Partition box with a required Scope choice (each selected Partition, or All selected partitions), gated by the dialog's `MudForm`.
- **Elsewhere:** the Operations queue dialog lists a disabled Run Profile but will not queue it; the Full Synchronisation preview's Run button leaves out disabled Run Profiles and, where every one is disabled, shows a disabled button with a tooltip saying why; the "Run now" preselection skips disabled Run Profiles; the Schedule editor marks a disabled Run Profile.

## Implementation Phases

### Phase 1: Model and migration ✅

- `Enabled`, `DisabledReason`, `DisabledPartitionName`, the enum, `RunProfileEnablement`, migration `AddRunProfileEnabledState`, repository copy of the three fields and the stored-state projection.
- Tests: `RunProfileEnablementTests` (JIM.Models.Tests): the wording of each reason, the refusal, and the re-enabling rule; `RunProfileEnabledStatePersistenceDatabaseTests` (RequiresPostgres, NoTracking): existing rows migrate enabled, and an update persists the three fields.

### Phase 2: Refuse a disabled Run Profile everywhere it can start ✅

- Tests: `TaskingServerDisabledRunProfileTests.CreateWorkerTaskAsync_DisabledRunProfile_RefusesWithTheReason` (and the enabled control); `SchedulerServerSafeStartTests.StartScheduleExecutionAsync_StepRunsADisabledRunProfile_RecordsAFailedActivityWithTheReasonAsync`; `WorkerDisabledRunProfileTests.RefuseIfDisabledAsync_*`.

### Phase 3: Enabling and disabling by update ✅

- Tests: `ConnectedSystemRunProfileEnablementTests` (JIM.Worker.Tests): disabling records `Manual`; enabling clears the reason; re-enabling a `PartitionRemoved` Run Profile without a scope is refused, with a Partition or `allSelectedPartitions` succeeds; `ConfigurationSnapshotService` records the disabled state.

### Phase 4: The refresh ✅

- Tests (RequiresPostgres, driven through the server on a NoTracking context, as JIM.Web runs it): `RefreshHierarchy_PartitionARunProfileTargetsIsRemoved_DisablesTheRunProfileAndClearsItsPartitionAsync` (the issue's reproduction, red against today's silent widening); `..._Cancel_AppliesNothingAndCompletesTheActivityWithAWarningAsync`; `..._NoRunProfileAffected_Cancel_StillAppliesAsync`.

### Phase 5: REST ✅

- Tests (`SynchronisationControllerRunProfileTests`, `SynchronisationControllerHierarchyTests`): DTO fields; enable/disable; 400 for re-enabling without a scope; `allSelectedPartitions`; hierarchy body defaulting to Disable; 409 with the affected Run Profiles for Cancel; execute refused for a disabled Run Profile.

### Phase 6: PowerShell

- Pester: `Set-JIMRunProfile -Enabled $false/$true`, `-AllSelectedPartitions`; `Import-JIMConnectedSystemHierarchy -RunProfileAction` (default Disable, Cancel sent, warning naming disabled Run Profiles); `Invoke-JIMApi` expanding a 409's affected Run Profiles.

### Phase 7: Portal

- Tests: `AwaitingSynchronisationRunProfileTests.Preferred_DisabledDeltaSynchronisation_FallsBackToAnEnabledRunProfile`; `RemovedPartitionRunProfilesDialogTests` (bUnit, Shared component): lists each Run Profile and its Partition, Disable closes with the choice. The rest is markup over `RunProfileEnablement`, verified by build and by eye.

### Phase 8: Documentation, changelog, close

- `docs/configuration/run-profiles.md`, `docs/configuration/partitions-and-containers.md`, the PowerShell reference (regenerated), the changelog, and this plan to `done/`.

## Success Criteria

- A refresh never changes what a Run Profile reads without the administrator's choice, on any surface.
- A disabled Run Profile cannot run from the portal, the REST API, PowerShell or a Schedule, and each says why.
- Re-enabling a Run Profile a removed Partition disabled always states its scope.

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| A cancelled refresh's merge, held in tracked memory, is written by the Activity's own save | Clear the change tracker before completing the Activity (the failure path already does, for #2742); a RequiresPostgres test asserts nothing changed in the database |
| The update path compares the caller's edit with itself on a tracking context (the Worker tracks) | Read the stored state as a projection, which never returns the tracked instance |
| A Schedule step queued before its Run Profile was disabled still runs | The Worker refuses it at execution, failing the Activity before any step is declared |
| A conditionally shown required field leaves `MudForm.IsValid` stale when it disappears | Revalidate the form when the Enabled switch changes |
| Integration scenarios read the Run Profile and hierarchy responses | The changes are additive; a refresh that removes no targeted Partition behaves as before. Readers are listed in the PR |
| Unit tests on mocks return null for the new stored-state read | Null means "no stored record", which applies the incoming state as given, so existing controller tests are unaffected |

## Departures

| From the brief | What was done instead | Why |
|----------------|------------------------|-----|
| "Portal Run buttons ... and its Run action is disabled with a tooltip" on the Run Profiles tab | The tab has no Run action today, so none is added; the Disabled chip carries the reason. The places that do start runs (Operations queue dialog, Full Synchronisation preview, "Run now") refuse a disabled Run Profile visibly | Adding a Run action to the tab is new scope |
| REST run endpoint "a 4xx in the existing error shape" | 400 with `ApiErrorResponse`, as every other queue refusal | One refusal shape for one endpoint |
| Re-enabling must carry a Partition or the all-partitions flag | Enforced as "the result has a Partition, or the change states all selected partitions". A Partition chosen in an earlier change while the Run Profile stayed disabled counts | Such a Partition was picked by an administrator after the removal, so nothing widens; refusing it would make the administrator restate a choice already saved |
