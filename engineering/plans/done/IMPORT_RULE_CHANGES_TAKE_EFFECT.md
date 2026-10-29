# Import Rule Changes Take Effect at the Next Synchronisation

- **Status:** Done
- **Issue:** [#2615](https://github.com/TetronIO/JIM/issues/2615) (parts B and C; part A ships separately)
- **Note:** Predicted against actual counts moved to [#2618](https://github.com/TetronIO/JIM/issues/2618): honest only when one change with an informing preview is behind the run.

## Overview

Saving a change to an import Synchronisation Rule, or to an Object Matching Rule, reaches existing objects only when a Full Synchronisation happens to run: a Delta Synchronisation visits only Connected System Objects whose own data changed. This plan makes such a save record that the change is **awaiting synchronisation** for the affected Connected System Object Type. The Connected System's next synchronisation, Delta or Full, processes every object of that type (and only changed objects of the others), then marks the change applied. The administrator sees when the change will take effect before saving, sees it pending until the run, and sees which run applied it afterwards.

## Decisions (agreed on the issue)

1. **Scoped to the Object Type**, never the whole Connected System.
2. **Classified by the existing grading.** A Destructive or Sync-affecting change (`ConfigurationChangeClassifier`) is recorded as awaiting synchronisation; a Cosmetic change is not. A change JIM cannot classify (configuration change tracking off, capture failed) is recorded: processing a type needlessly costs time, missing one leaves objects wrong.
3. **A running synchronisation never consumes a newer request.** The run claims, in one statement, the requests already committed before it loads its rules; on success it marks exactly those applied. A request saved mid-run stays pending. No timestamps are compared across hosts.
4. **Repeated changes collapse.** Many changes awaiting synchronisation for one type mean the type is processed once; each keeps its own row so the run can list every change behind it.
5. **Export follows on its own.** Processing every object of the type reprocesses every Metaverse Object the type joins or projects, and export evaluation runs on them as usual. No second export mechanism.

## Technical Architecture

### Data model

New entity `ConfigurationChangeSynchronisation` (table `ConfigurationChangeSynchronisations`), one row per saved change per Object Type. Product vocabulary is synchronisation: what runs is the administrator's ordinary Delta or Full Synchronisation, never a new kind of run:

| Column | Notes |
|---|---|
| `Id` (bigint identity) | |
| `ConnectedSystemId` | FK, cascade on Connected System delete |
| `ConnectedSystemObjectTypeId` | FK, cascade when the type is deselected or deleted |
| `RequestedAt` | |
| `ConfigurationChangeActivityId` | FK to the save's Activity, set null on delete; the "why" |
| `SyncRuleId` (nullable) | FK, set null on delete (history outlives the rule, as #1990) |
| `Reason` | Snapshot: what changed, e.g. rule name and change class, so the row reads after the rule is gone |
| `ClaimedByActivityId` (nullable) | The run applying it; overwritten if that run fails and a later one claims it |
| `AppliedAt` (nullable) | Set on success; null means pending |

Partial index on `(ConnectedSystemId) WHERE "AppliedAt" IS NULL`. Delete-path coverage (`DeletePathForeignKeyCoverageTests`), factory reset cascade, and Connected System deletion sequence placement per `src/CLAUDE.md`.

### Creating requests (application layer)

One private method in `ConnectedSystemServer`, called after configuration change capture at every save that can change an import outcome:

- whole-rule create and update (both initiator overloads; this also covers enable/disable, projection, Inbound Out-of-Scope Action and every Scoping Criteria endpoint, which all route through the whole-rule save);
- Attribute Flow mapping create, update, delete, and priority reorder;
- Object Matching Rule create, update, delete, in Advanced Mode (rule's type) and Simple Mode (the Connected System Object Type the rule is on; today's capture collapses this to the Connected System, so the type id must be carried through);
- matching mode switch (every type with an import rule);
- schema refresh disabling a rule.

Only import rules record changes awaiting synchronisation. The capture wrappers return a `ConfigurationChangeCaptureOutcome` (what the capture found: Created, Changed with its class and the sections that changed, NoChange, NotTracked, or Failed), because the Activity cannot say it: its class stays unset for a create, and tracking off or a swallowed capture failure leave no trace on it. `ConfigurationChangeSynchronisationPolicy` decides from the outcome: Changed records unless Cosmetic; Created records when the rule is enabled; NotTracked and Failed record, even for a disabled rule, because nothing was compared; NoChange does not. An empty diff requests nothing. The row is written after the save has been written, last in the save, so the worker's claim can rely on every claimed row already being in the rules it loads.

A Simple Mode Object Matching Rule belongs to a Connected System Object Type, so the type is carried through from the rule, and recorded only when an enabled import rule covers it (nothing else synchronises its objects). A matching mode switch records every type an enabled import rule covers. A configuration test case save opts out of recording (its saves capture history only), since with tracking off the capture could not grade it.

A request that cannot be recorded fails the save's Activity with a message naming the remedy (run a Full Synchronisation) and throws `ConfigurationChangeSynchronisationNotRecordedException`, never silently. The configuration change itself has already been saved and stays.

Deleting an import rule is out of scope: deletion recall already handles it.

### Consuming requests (worker)

- **Claim** at run start, before rules load, and only the rows the worker offered. The worker reads the ids awaiting synchronisation for the Connected System BEFORE it loads the Connected System (`Worker.LoadConnectedSystemForSynchronisationAsync`), hands them to the processor, and the claim is `UPDATE "ConfigurationChangeSynchronisations" SET "ClaimedByActivityId" = @run WHERE "Id" = ANY(@eligibleIds) AND "AppliedAt" IS NULL RETURNING "Id", "ConnectedSystemObjectTypeId"`. A row is written after its rule change commits, so every claimed row was committed before anything the run uses was loaded (the Connected System with its Object Types and Simple Mode Object Matching Rules, then the Synchronisation Rules); a row committed after the early read waits for the next run.
- **Delta:** the count adds the full count of claimed types; the early exit also waits for claimed requests; `CreateTypeOrderedPagerAsync`'s loader selects every object for a claimed type and modified objects for the rest. The closing sweep is unchanged.
- **Full:** claims everything pending; processes as today.
- **Apply** on success, beside `UpdateDeltaSyncWatermarkAsync`: `SET "AppliedAt" = now() WHERE "ClaimedByActivityId" = @run AND "AppliedAt" IS NULL`. Cancelled or failed runs apply nothing.
- **Synchronisation preview** (`SyncPreviewServer`) reads pending requests without claiming, so a previewed Delta matches what the run would do (`SYNC_PREVIEW_ZERO_SIDE_EFFECTS.md`).
- Information log per run: the types processed in full and the number of changes behind each.

### Configuration drift indicator

`ConfigurationDriftService` reports Sync-affecting changes since the last Full Synchronisation. Once a Delta applies import-rule changes, counting them there is false. Exclude change Activities recorded as awaiting synchronisation (the query leaves out any Activity a recorded row names, applied or not): pending ones show as **Changes awaiting synchronisation** instead (`ConfigurationDriftStatus.AwaitingSynchronisation`: Object Type, number of changes, earliest request, read in one extra query for the whole batch, and reported even when tracking is off or the system has never had a Full Synchronisation), applied ones are done. The indicator keeps covering what JIM still does not apply by itself (export Attribute Flow edits, Metaverse Attribute and Object Type changes). An export rule's scope and Deprovisioning Action are applied by the export review the save flags, so the save judges them to need no Full Synchronisation: it records that on its Activity (`Activity.ConfigurationChangeNeedsFullSynchronisation`, true when a consequential section is applied by neither a recorded Object Type nor the review, false when synchronisation applies it all, null when nothing was compared or the change predates the column), from the same function the acknowledgement names the sections with, and the query counts only Activities where it is not false. One save carrying both a scope change and an Attribute Flow edit is one Activity, true for the part the review does not carry.

## User experience (part C)

1. **Before saving.** The acknowledgement dialog's unconditional "Run a Full Synchronisation..." is replaced, for import changes, with: "Takes effect at the next synchronisation of {System}, which will process every {Type} object ({count}; about {N} min at the speed of its last Full Synchronisation)." The estimate uses the type's object count over the system's last Full Synchronisation rate; omitted when there has been none. REST and PowerShell callers get the same in the save response (`SyncRuleSaveResponse.AwaitingSynchronisation`), since they have no dialog.
2. **Between save and run.**
   - Connected System page: a notice, "Changes awaiting synchronisation: {Type}", per type, listing each change behind it (linked), and **Run now** (opens the existing queue dialog with the system's synchronisation Run Profile preselected).
   - Synchronisation Rule page: a chip on the identity strip, "Awaiting synchronisation", linking to the notice.
   - Export side: "Export review pending: N" on the Metaverse Object Type's schema page (`/admin/schema/object-types/{id}`, never the `/t/{type}` list end users browse) and on export rules; "Awaiting export review" on a Metaverse Object's page, inside `<AuthorizeView Roles="Administrator">` like the generated value banner (`Pages/Types/View.razor`), and not loaded for anyone else. The REST field is already administrator-only (`MetaverseController` is `[Authorize(Roles = "Administrator")]`); a bUnit test asserts the panel is absent for a User-only principal.
3. **After the run.**
   - The synchronisation Activity: a section "Processed every {Type} object because" listing the changes (linked).
   - The configuration change Activity: "Applied by {Activity}" (or "Pending: next synchronisation of {System}").
   - Objects whose outcome moved already get execution items (joined, projected, disconnected, Attribute Flow); no new item type is needed.

**Surface parity:** every new read is on the Connected System, Synchronisation Rule, Metaverse Object Type, Metaverse Object and Activity DTOs, and so on `Get-JIMConnectedSystem`, `Get-JIMSyncRule`, `Get-JIMMetaverseObjectType`, `Get-JIMMetaverseObject` and `Get-JIMActivity` output (documented per `src/JIM.PowerShell/CLAUDE.md`). **Run now** already exists on REST (`POST .../run-profiles/{id}/execute`) and PowerShell (`Start-JIMRunProfile`).

## Implementation Phases

1. ✅ **Engine**: entity, migration, repositories (Postgres, in-memory, read-only guard), claim and apply, Delta and Full consumption, early exit, synchronisation preview. Workflow tests: a save leads to a Delta processing every object of that type and only changed objects of others; a save during a run is not consumed by it; ten saves mean the type is processed once, with ten changes listed; a failed run applies nothing. `RequiresPostgres`: claim and apply SQL, delete paths.
2. ✅ **Requests**: every save path above, class-driven, Simple Mode type carried through, loud failure. Tests per path.
3. ✅ **Drift indicator** rework.
4. ✅ **Read surfaces**: portal notices and chips, Activity sections, DTOs, cmdlet output, Pester tests. Artefact screenshots (taken at the runtime check).
5. ✅ **Before saving**: dialog wording, estimate, save response. The acknowledgement and the save ask the same policy functions with the same inputs (`ConfigurationChangeSynchronisationPolicy.RecordsAwaitingSynchronisation`, `FlagsExportReview`), and an end-to-end agreement suite runs both for every branch. Saves answer with what they recorded: `AwaitingSynchronisation` on the Synchronisation Rule, Attribute Flow mapping and Object Matching Rule responses, one entry per Object Type with its object count and, where the Connected System has completed a Full Synchronisation, about how long. Scoping Criteria saves and the deletes that answer 204 (Object Matching Rule, Scoping Criteria) do not carry it yet; the Connected System's page and `Get-JIMConnectedSystem` list what is waiting.
6. ✅ **Docs**: rewrite (not append) the pages that say "run a Full Synchronisation after changing configuration": `concepts/synchronisation-pipeline.md` (Full Sync vs Delta Sync), `configuration/connected-systems.md` (Configuration changes pending), `configuration/configuration-changes.md`, `configuration/synchronisation-rules.md`, `configuration/scoping-criteria.md`, `administration/troubleshooting-synchronisation.md`, `concepts/attribute-priority.md`, `configuration/attribute-mappings.md`; plus the "when a configuration change takes effect" flow diagram from part D. Changelog.

## Validation

Unit and `RequiresPostgres` tests per phase; runtime check on the sandbox stack (save an import rule scope change, run a Delta, confirm the type was processed in full and the Activity says why); integration Scenarios 008 and 032 at Medium (export staging is reached), and a multi-page template per the single-page rule.

## Risks

- **A scheduled Delta overruns its window** after a change. Mitigated by stating the cost before saving and showing it pending; this is the trade the issue chose.
- **A failed run redoes the whole type** next time (no per-page resume, unlike per-object flags). Same as Full Synchronisation today.
- **Configuration change tracking off** means every import save is recorded as awaiting synchronisation. Documented.
