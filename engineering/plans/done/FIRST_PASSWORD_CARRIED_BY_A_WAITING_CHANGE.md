# An Account's First Password Carried by a Password Change Already Waiting

- **Status:** Done
- **Note:** The `RequiresPostgres` tests (written, not yet run) and runtime verification on Samba AD (the scenarios under Verification) are still to run; the migration's Designer must be regenerated after merging `main`.
- **Issue:** [#2160](https://github.com/TetronIO/JIM/issues/2160)
- **Created:** 2026-10-13

## Overview

When JIM provisions an account while the person's own password change (or an administrator's set naming the account) is already waiting on the Password Synchronisation queue, export staging used to let the waiting change win and throw the Initial Password's enable decision away. Delivery then treated the surviving row as an ordinary propagated or explicit change, so nothing enabled the account: where no `userAccountControl` Attribute Flow exists, the account stayed at the directory's default (disabled) with nothing to say why. The reverse order (a change arriving after the Initial Password was queued but before it landed) lost the decision the same way, because supersession replaced the rule and the mark with the newer change's nulls.

## The principle

**Passwords replace each other; promises about the account carry forward.** The surviving change delivers the newest password, and carries the Initial Password's promises about the account: both account-flag decisions the provisioning Synchronisation Rule would make (Enable Account for `0x2`, and the rule's expiry for "Password never expires", `0x10000`), under `AccountFlagsPolicy`'s ownership ladder, so an export that wrote `userAccountControl` still outranks them. It never carries the Initial Password's password, nor "must change at next sign-in", which belongs to the password being delivered.

## Decisions (agreed 2026-10-13)

1. **Option A of the design, with the critique's changes.** A new nullable column, `PendingPasswordChanges.ProvisionedConnectedSystemObjectId` (FK to the Connected System Object, `ON DELETE SET NULL`, filtered index), names the account a row owes its first password. It is set on every Provisioned row and on a change carrying a first password; `SyncRuleId` and `EnabledStateSetByAttributeFlow` complete the obligation. The migration backfills it for Provisioned rows already queued.
2. **Stage in one statement.** `StageProvisionedPasswordChangesAsync` decides per account in one `INSERT ... ON CONFLICT DO UPDATE`: the column assignment is one row from a sub-select that decides "take over" once, the `WHERE` declines the conflicting case, and `RETURNING` reads the row as it stood (PostgreSQL 18's `OLD`) so the staging Activity describes exactly what was decided. Nothing returned means Conflicted and nothing else; no lane can delete the row between two statements.
3. **Which waiting rows carry the obligation.** Pending and Delivering rows, and rows parked for a missing account or after their transient retries ran out (the Create has just proved both wrong): a propagated change for the Object Type the system's Password Synchronisation targets, or an administrator's set naming the account. They are released (claim taken back, park released, attempts against a missing account reset), and their window extends to the Initial Password's.
4. **Which lose to the Initial Password.** Expired or cancelled rows; rows parked for a policy rejection, an unreadable password or an unsupported operation; an administrator's set whose account is gone or disjoined; an Initial Password for an account gone or the same; and a propagated change held because Password Synchronisation is switched off.
5. **Paused Password Synchronisation: the Initial Password wins when the waiting change is held.** A held password has not reached that system, and a first password is delivered whatever the pause (decision D1). Consistently, a propagated change for a paused system is not queued over a first password still owed there (`QueuePasswordChangesAsync` declines it and returns it; `SetPasswordAsync` drops that system from the result and the Activity says why). A change already carrying a first password is delivered like the Initial Password it stands in for, so a pause switched on afterwards cannot strand the account or let the obligation expire unseen.
6. **Conflicted.** A live change that cannot reach the new account holds the queue's one row: nothing is written, the export records an Activity completed with an error, logs the Connected System Object ids, counts it on `InitialPasswordsConflictedCount`, and warns with its own run message. A person has at most one joined Connected System Object per Connected System (`IX_ConnectedSystemObjects_ConnectedSystemId_MetaverseObjectId_Unique`) and the new account is joined to them, so no row can owe another live account of theirs, and an administrator's set or an earlier Initial Password naming another account is for one that is gone or disjoined, which gives way (decision 4). The one reachable case is a propagated change for the Object Type Password Synchronisation targets when the new account is of another: it cannot be delivered while the new account is the person's, so the remedy is to cancel it, then set a password on the account.
7. **Supersession keeps the obligation.** A newer change bringing no first password of its own keeps a live row's obligation (`PendingPasswordChange.Supersede`, and the same rule as `CASE` assignments in the coalescing UPSERT from `PendingPasswordChangeBulkColumns.PendingPasswordChangesInitialPasswordObligation`). A newer Initial Password brings its own; a dead row's is dropped.
8. **The mark is written and read for every obligation.** `MarkOwedInitialPasswordsAsSetByAttributeFlowAsync` matches the owed account, and still matches a Provisioned row by its own account (belt and braces); delivery re-reads the mark for any row owing a first password.
9. **Delivery.** A carried row resolves its account as before; where it reaches the owed account and the rule still sets an Initial Password, the rule's promises go to the policy (`InitialPasswordAccountFlags`), and the expiry sent follows the decision only where it writes "Password never expires" (`AccountFlagsPolicy.ExpiryToSend`). The rule no longer setting one, or the change reaching another account, delivers the password without the promises and says so. The Activity claims "enabled" only where the Connector applied everything asked.
10. **Behaviour change (accepted).** An administrator's set with no enable choice, on an account still owed its first password, follows the rule's Enable Account.
11. **The Create race.** A delivery owing no first password waits, counting no attempt, while its account's own Create is in flight (`AccountEnablementFacts.CreateInFlight`), so it cannot land mid-batch and let staging queue an Initial Password over it.
12. **Surfaces.** A "First password" marker on Operations > Passwords and the identity's Password panel; `provisionedConnectedSystemObjectId` on the REST queue response and so on `Get-JIMPendingPasswordChange`; cancel confirmations (the queue row, the panel's Stop trying, and Stop trying in the set-password dialog from the identity and from the Connected System Object page) warn that cancelling leaves the account without a password from JIM; enable documentation for administrator sets and the rule on all three surfaces. The staging Activities read as notes on the Password History timeline.

## Implementation

- **Model:** `PendingPasswordChange.ProvisionedConnectedSystemObjectId`, `OwesInitialPassword`, `CarriesInitialPassword`, `IsHeldWhilePasswordSynchronisationIsOff`, `KeepsInitialPasswordObligationOver`, `CarryInitialPassword`; `ProvisionedPasswordStagingDisposition.Conflicted`; `InitialPasswordStandIn`; `InitialPasswordAccountFlags`; outcome DTO facts (`Existing*`).
- **Data:** migration `AddPendingPasswordChangeCarriedObligation` (column, FK, index, backfill); bulk column lists; both UPSERTs; the mark; the held-class predicates in claim, expiry, due, outlook and summary; the header projection; `CreateInFlight` in the facts query; the in-memory twin.
- **Application:** staging outcomes and Activities (`InitialPasswordStagingMessages`), counters and summary log; delivery (`ResolveFirstPassword`, the policy input, `DescribeDelivery`, lane counters); `SetPasswordAsync`'s not-queued systems.
- **Worker:** the Conflicted run warning.
- **Web, REST, PowerShell, docs, changelog.**

## Verification

- Unit: `AccountFlagsPolicyTests`, `PendingPasswordChangeTests`, `PendingPasswordChangeHeaderTests`, `InitialPasswordStagingTests`, `PasswordDeliveryTests`, `SetPasswordRequestTests`, `ExportOutcomeMessageTests`, `BulkInsertColumnCompletenessTests`, `PendingPasswordChangeDisplayTests`, `PasswordHistoryTimelineModelTests`, the REST queue tests, Pester.
- `RequiresPostgres`: `PasswordSynchronisationQueueDatabaseTests` (round trips, staging dispositions, supersession, the paused refusal, the mark, the claim over a paused system, the FK) and `PendingExportAwaitsInitialPasswordDatabaseTests` (`CreateInFlight`).
- Runtime on Samba AD (to run): a propagated password set for a person synchronised but not yet exported, then the export; the account is enabled, pwdLastSet is not 0, a bind with the person's password succeeds, and both Activities say what happened. Variants: the rule's Enable off; Password Synchronisation switched off before provisioning; the reverse order; a leaver disabled before delivery.

## Risks

- **Expiry is extended.** A carried change's window grows to the Initial Password's.
- **One row per person per system still applies.** A propagated change for another Object Type than the new account's gives Conflicted rather than a silent loss, and needs an administrator to cancel it.
- **Upgrade cannot repair history.** Accounts that lost the enable before this fix stay as they are.
- **A Delivering row taken back sends the same password twice**, harmlessly, with two child Activities.
