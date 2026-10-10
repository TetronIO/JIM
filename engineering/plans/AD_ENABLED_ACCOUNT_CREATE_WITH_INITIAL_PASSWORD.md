# Creating an Enabled Active Directory Account When an Initial Password Follows

- **Status:** Planned
- **Issue:** [#2039](https://github.com/TetronIO/JIM/issues/2039)
- **Created:** 2026-10-10

## Overview

Active Directory refuses to create an enabled user (`userAccountControl` 512) with no password: `0000052D: SvcErr: DSID-031A12C5, problem 5003 (WILL_NOT_PERFORM)`, ERROR_PASSWORD_RESTRICTION. Samba AD accepts it, so the Samba lab never showed this; the Windows AD lab (#1853) did, on Scenario 001's Joiner.

JIM's Initial Password (#1121) does not help on its own, because it is staged only after the Create succeeds (`ExportExecutionServer.StageInitialPasswordsForBatchAsync`, called from the batch success path after the CSO external ids are assigned), so the Create still carries 512 and no password. The only configuration that works today is Scenario 017's: no `userAccountControl` Attribute Flow, with the Initial Password's `EnableAccount` owning the enabled state. That rules out driving enable and disable from source data, which Scenario 001's Tests 2d/2e and many customers' rules do.

The agreed direction (option A in the issue): create the account disabled when an Initial Password will follow, let the delivery enable it, and have the confirming import treat the flow's enabled value as the target rather than reporting the interim disabled value as unconfirmed.

## Business Value

- Joiners provisioned into real Active Directory with an enabled `userAccountControl` flow work, instead of failing every Create.
- Enable and disable stay data-driven through `userAccountControl`, as on Samba today.
- Without an Initial Password, the failure names the remedy instead of a bare `0000052D`.

## Technical Architecture

### Current state (verified against source)

- **Create path.** `LdapConnectorExport.BuildAddRequestWithOverflow` builds the `AddRequest` for both the sync and async paths. Every single-valued attribute, `userAccountControl` included, travels in the add itself. No create-time `userAccountControl` handling exists; `ProtectedAttributeDefaults` holds only `accountExpires`.
- **Directory type.** `LdapDirectoryType.ActiveDirectory` versus `SambaAD` is decided from the rootDSE (`LdapConnectorUtilities.DetectDirectoryType`). The `LdapConnectorExport` constructor defaults to `ActiveDirectory`, so tests must pass the type explicitly.
- **Export results.** `ConnectedSystemExportResult` carries no per-attribute "value actually written". `FailureFor` classifies only uniqueness rejections; everything else becomes `General`, which `SyncExportTaskProcessor.ToExecutionItemErrorType` maps to `UnhandledError` and fails the Activity as though JIM had a defect.
- **Password delivery.** `LdapConnectorPassword.ApplyUserAccountControlFlags` clears `0x2` when `EnableAccount` is true, after the password is set. `0000052D` detection precedent: `LdapConnectorPassword.ErrorPasswordRestrictions`.
- **Initial Password lookup.** Per Synchronisation Rule: `ISyncRepository.GetSyncRuleIdsWithInitialPasswordEnabledAsync(provisioningRuleIds)`.
- **Three paths would reassert 512 and re-trigger `0000052D`** unless guarded:
  1. The confirming import sees 514, the change is unconfirmed, and it is retried.
  2. Expression mappings are relevant on any Metaverse Object change, so an HR change re-stages 512; the merge-replace branch in `ExportEvaluationServer.CreateOrUpdatePendingExportWithNoNetChangeAsync` rebuilds the Pending Export with the incoming change.
  3. Drift detection stages a correction, and `SyncTaskProcessorBase.FlushPendingExportOperationsAsync` deletes every existing Pending Export for a CSO that gets a new one.

### Latent defect found while designing (fix in its own layer first)

The merge-replace branch clones surviving changes with new ids (`ExportEvaluationServer.cs`, the `driftOnlyChanges` projection) but copies neither `Status`, `ExportAttemptCount` nor `LastExportedAt`. `SyncEngine.SelectSurvivingDriftChanges` deliberately keeps a change that is `ExportedPendingConfirmation`; the clone resets it to `Pending`, so it is re-sent and its attempt count restarts. This design relies on that branch preserving a held change, and the defect also affects every directory today (a re-sent multi-valued Add meets a value already present). Prove it with a failing test and fix it as the bottom layer, then run Scenario 008 at Medium per `src/JIM.Application/CLAUDE.md`.

### Proposed solution

**1. Where "an Initial Password will follow" is known (engine decides, connector acts).** Before each export batch, run `GetSyncRuleIdsWithInitialPasswordEnabledAsync` once over the batch's Creates and set a transient `[NotMapped] bool InitialPasswordFollows` on each qualifying `PendingExport` (precedent: `PendingExportAttributeValueChange.PendingGeneration`; copy it in `WithChanges`). Hand the same set to `StageInitialPasswordsForBatchAsync`, so the lookup moves rather than multiplies. The connector never sees rule ids or password material.

**2. The create rewrite (connector).** In `BuildAddRequestWithOverflow`, OR `0x2` into `userAccountControl` only when the directory is `LdapDirectoryType.ActiveDirectory` (not the AD family, so Samba is unchanged), the change is a Create, `InitialPasswordFollows` is set, and the value is a single integer without `0x2`. Never mutate the change itself. Report it back through a new `ConnectedSystemExportResult.AttributesAwaitingInitialPassword`, which every other connector leaves null.

**3. Recording the interim value.** New persisted `bool AwaitsInitialPassword` on `PendingExportAttributeValueChange` (migration; `PendingExportBulkColumns` insert, export-result and confirmation lists; `BulkInsertColumnCompletenessTests`; a `RequiresPostgres` round trip; a partial index `WHERE "AwaitsInitialPassword"`). Set it in `ProcessBatchSuccessAsync` from the result. The status stays `ExportedPendingConfirmation`, so the existing in-flight protections apply unchanged.

**4. The confirming-import rule** (in `SyncEngine.ReconcileCsoAgainstPendingExport`), for a change awaiting an Initial Password:

| Imported value | Password owed for this account? | Outcome |
|---|---|---|
| Equals the flow's value | Either | Confirmed, as today |
| Differs | Yes (`Pending`, `Delivering` or `Parked`) | **Held**: unchanged, not retried, no RPEI, counted in the batch summary |
| Differs | No | **Released**: back to `Pending`, the flag cleared; the next export writes the flow's value as an ordinary Update |

"Owed" is keyed on the CSO's `(ConnectedSystemId, MetaverseObjectId)`, matching the unique index on `PendingPasswordChange`, so a live Explicit or Propagated row that won coalescing counts. The lookup is a new raw-SQL `ISyncRepository.GetMetaverseObjectIdsOwedPasswordAsync`, run per page only when a page holds a flagged change.

**5. Guards.** Drift skips a CSO with a held change entirely (a per-attribute skip is not enough, because the flush deletes the whole Pending Export). The merge-replace branch drops an incoming change equal to a held one, preserves held changes' status and flag (layer 0 fixes the general case), and lets an incoming change with a different value supersede.

**6. Without an Initial Password.** Do not pre-refuse (a domain whose policy permits an empty password accepts the Create). Classify the failure instead: in `FailureFor`, AD family plus `0000052D` on a Create or an Update carrying `userAccountControl` maps to a new `ConnectedSystemExportErrorType`, mapped to a new execution item error type so it no longer reads as a JIM defect. The message names both remedies: configure an Initial Password on the provisioning Synchronisation Rule, or create the account disabled.

### Failure and expiry

The account stays disabled throughout; nothing is lost.

| Password state | Export side | Where the administrator sees it |
|---|---|---|
| Transient failure (still `Pending`, backing off) | Held | The password delivery Activity |
| `Parked` | Held until Retry or a rule edit releases it; delivery then enables, and the next import confirms | The Parked surfaces under Operations > Passwords |
| Expired, Withdrawn, never staged, or no Metaverse Object | Released on the next import that returns the CSO; the Update to the flow's value fails with the new actionable error and backs off to `Failed` | The export's execution item |
| Coalesced into a Propagated change | Delivered, released, reassert succeeds | Nothing to see: it converges |

Caveat: an expired password changes nothing in Active Directory, so a Delta Import never returns the account; the release waits for a Full Import.

## Open Decisions

1. **Trigger.** Rewrite when the Synchronisation Rule's Initial Password is enabled (recommended), or only when it also has `EnableAccount` set. With the first, `EnableAccount = false` still converges: the delivery sets the password, the hold releases, and the flow's 512 is then accepted because a password exists.
2. **While Parked.** Hold silently (recommended), or release and let the export fail visibly beside the Parked password.
3. **On release.** Release silently (recommended): the interim value was deliberate. Or raise a "not confirmed, will reassert" warning.
4. **Connection state.** Keep the ordinary Update Pending state for a held account (recommended for now), or add an "Awaiting Initial Password" state across portal, REST and PowerShell.
5. **Scope.** Real Active Directory only, as the acceptance criteria say (recommended), or the whole AD family including Samba.
6. **Pre-existing, separate issue.** With `EnableAccount = true`, an account the flow deliberately creates disabled is enabled by the delivery until drift corrects it. It happens on Samba today; track it separately.

## Implementation Phases

### Phase 0: Preserve held changes through merge-replace (own stack layer)

Failing test for the clone dropping `Status`, `ExportAttemptCount` and `LastExportedAt`; fix; Scenario 008 at Medium.

### Phase 1: Actionable `0000052D` and the create rewrite

New export and execution item error types with labels on every surface; `FailureFor` classification; `InitialPasswordFollows` and the batch lookup; the connector rewrite and its result field. Unit tests first: rewrite 512 to 514 and 66048 to 66050; no rewrite for Samba AD, no Initial Password, already disabled, absent `userAccountControl`, or an Update; the classification in both exception forms.

### Phase 2: Hold and release

`AwaitsInitialPassword` (migration, bulk columns, round trip), the reconciliation rule, the owed-password lookup, and the drift and merge guards. Unit tests: confirm, hold, release, unflagged changes unaffected, Parked counts as owed, no `ExportNotConfirmed` item on a hold, no lookup when nothing is flagged.

### Phase 3: Integration and documentation

`Setup-Scenario-001.ps1 -InitialPassword` (keeps the `userAccountControl` flow, adds a static Initial Password with `EnableAccount`), enabled for the Active Directory lab only; Joiner asserts 512 after the password queue drains. Regression: Samba, OpenLDAP and 389 Directory Server unchanged; Scenario 017 on Samba and Active Directory; Scenario 008 at Medium. Docs: the LDAP Connector page (export features, Setting Passwords, Export failures), Synchronisation Rules (Initial Password), Passwords and JML concepts, the export execution flow diagram, and the comment in `Setup-Scenario-017.ps1` that says the flow must be removed.

## Success Criteria

- Scenario 001 on the Windows AD lab, with an Initial Password, passes Joiner and Tests 2d/2e.
- Samba AD and OpenLDAP behaviour unchanged (the rewrite is gated on real Active Directory, and every engine path is inert unless a change is flagged).
- No `ExportNotConfirmed` item and no reassert while a password is owed; an actionable error when none is.

## Risks & Mitigations

- **Sync integrity.** The hold changes reconciliation for flagged changes only, behind a partial index; Phase 0 and Scenario 008 cover the merge path.
- **AD LDS** also maps to `ActiveDirectory`. Its own users are disabled through `msDS-UserAccountDisabled` rather than `userAccountControl`, so the rewrite (which needs a `userAccountControl` value on the Create) does not fire there unless a flow writes that attribute; check against AD LDS before claiming support for it.
- **Unverified on the lab:** that Active Directory stores 514 rather than 546 for an explicit 514 create. Check on the first lab run before relying on equality in tests.
