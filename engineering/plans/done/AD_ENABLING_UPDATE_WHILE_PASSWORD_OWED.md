# Enabling an Active Directory Account While Its Password Is Owed

- **Status:** Done
- **Note:** The Windows Active Directory lab run is owed: Scenario 024's `EnableWhileOwed` step (which also confirms that Active Directory answers an enabling modify on a password-less account with `0000052D`), Scenario 001 at Medium with `-InitialPassword` on the lab, and the Samba AD regressions (Scenario 001 at Medium with `-InitialPassword`, expecting no held changes; Scenario 017; Scenario 008 at Medium for the write-order change). The `RequiresPostgres` write-order test was written but not run in the implementing environment.
- **Issue:** [#2158](https://github.com/TetronIO/JIM/issues/2158)
- **Created:** 2026-10-13
- **Builds on:** [AD_ENABLED_ACCOUNT_CREATE_WITH_INITIAL_PASSWORD.md](AD_ENABLED_ACCOUNT_CREATE_WITH_INITIAL_PASSWORD.md) (#2039) and [INITIAL_PASSWORD_ENABLED_STATE_PRECEDENCE.md](INITIAL_PASSWORD_ENABLED_STATE_PRECEDENCE.md) (#2064)

## Overview

#2039 made a Create to Active Directory carry `userAccountControl` with ACCOUNTDISABLE added when an Initial Password follows it, held the change until the password was delivered, and had the delivery complete the enable. It did nothing for an **Update** that enables an account whose password has not been delivered yet. The common case is a pre-hire: created Archived (`514`), their Initial Password still queued, retrying or parked, and their start date arriving first. The Attribute Flow then asks for `512`, real Active Directory refuses it with `0000052D` (ERROR_PASSWORD_RESTRICTIONS), and the export backs off until it is Failed with "Active Directory will not enable an account that has no password".

Every downstream piece of #2039 and #2064 was already change-type agnostic (the import hold and release, the delivery's hold probe and `CompletesWithheldEnable`, the merge guards, optimistic apply's exclusion; holds on Update Pending Exports already existed after a held Create was confirmed). The gap was the Connector (which rewrote only Creates) and the flag the engine hands it (set only on Creates, with `MarkChangesAwaitingInitialPassword` rejecting a report on anything unflagged).

## Business Value

- A pre-hire whose start date arrives while their password is still owed is held disabled and enabled within seconds of the password landing, instead of the export failing and needing an administrator.
- Accounts that already have a password are enabled straight away, exactly as before.
- No new setting, schema change or query; one indexed read per batch with Updates, through a Connector that can set passwords.

## Technical Architecture

### Options considered

| Option | Summary | Why not chosen / chosen |
|--------|---------|-------------------------|
| A: Proactive withhold whenever any password is owed | Flag Updates from the owed lookup and rewrite `512` to `514` up front | Regression: holds accounts that already have a password (a returning leaver whose propagated change is parked stays disabled until it is fixed or expires) |
| B: Proactive withhold while the Provisioned password is owed | New per-account query; rewrite up front | Misses first passwords superseded or coalesced into a live change; holds accounts given a password outside JIM; needs a new repository method and its twins |
| **C: Engine permits, Active Directory decides** (chosen) | Flag Updates from the owed lookup; the Connector sends the flow's value and resends disabled only after Active Directory refuses it for want of a password | Holds only on proof that the account has no password; covers coalesced and superseded first passwords; flag and hold use the same test |

### Decisions

1. **Option C.** The engine flags an Update `InitialPasswordFollows` when the account's Metaverse Object is owed a password on the Connected System, reusing `ISyncRepository.GetMetaverseObjectIdsOwedPasswordAsync` (Pending, Delivering or Parked, any origin), the confirming import's own hold test, so the flag and the hold never disagree. `ExportExecutionServer.FlagCreatesFollowedByAnInitialPasswordAsync` became `FlagExportsFollowedByAPasswordAsync`, on all three paths (immediate, sequential deferred, parallel through the batch's own repository).
2. **The lookup is skipped where it cannot matter:** a batch with no Update to a joined account, and a Connector that does not implement `IConnectorPasswordManagement` (no password can be queued to complete anything). One lookup per batch at most.
3. **A failed owed lookup is contained** (`catch (Exception ex) when (ex is not OperationCanceledException)`): a Warning naming the Connected System, and the Updates go unflagged, so a refusal is reported on the export as it would be without the flag. The in-memory repository gained `FailOwedPasswordLookupWith` to test it.
4. **The resend is gated on the refusal, not on real Active Directory only.** The critique of the design overturned the first proposal's "real AD only, symmetric with #2039" gate: #2039's Create rewrite is proactive, so it needed a directory gate to keep Samba AD's behaviour unchanged; this resend is reactive, and `LdapAccountEnableRefusalClassifier.IsAccountEnableRefusal` already confines the refusal to the Active Directory family. Samba AD today never refuses, so it is unchanged; a Samba release that starts enforcing the rule gets the hold rather than the failure.
5. **The resend's guards:** an Update, flagged, refused as an account-enable refusal, whose refused request carries exactly one `userAccountControl` modification, a Replace of one invariant-culture integer without ACCOUNTDISABLE. The same modifications are sent again with that value OR'd with `0x2`, through the same `SendModify`/`SendModifyAsync` path (Permissive Modify and the fallback in parts apply), on the sequential and concurrent update paths, after a rename without repeating it. A refusal returned rather than thrown is raised by the Connector itself, so it takes the same path. A resend refused again fails with the original classification. The 0x2 test and OR is one helper shared with the Create rewrite (`TryWithholdEnablement`), whose doc now says the Create rewrite is the proactive half.
6. **`MarkChangesAwaitingInitialPassword` accepts the report for a flagged Update**, through the flag; its logic is unchanged.
7. **The delivery's wait is widened** (deliberately reversing two #2173 tests): `PasswordEnablementPolicy.WaitsForAnExportInFlight` is every decision except an administrator's choice. An export in flight may now be placing a hold this very delivery would complete; deciding before the hold is recorded would set the password without the enable, and the account would wait a schedule cycle. #2083 (batches never left Executing on failure or cancel) has landed, which bounds the cost of the wider wait.
8. **Write order.** The bulk Pending Export update writes the attribute value changes (including `AwaitsInitialPassword`) before the Pending Exports leave Executing. `UpdatePendingExportsAsync` already commits both statements as one (#2083), so there the order is invisible; it matters for `UpdatePendingExportAsync`, which runs them without a transaction, and a crash between the two now leaves sent changes under an Executing export, which `RecoverStrandedExecutingPendingExportsAsync` resolves to Exported.
9. **Text.** The delivery's `CompletesWithheldEnable` message says JIM held the enable back until the account had a password (not "created disabled"); the classifier's failure message also names the set-password action for an existing account; the MNT-1 sweep covers the policy class summary, enum and run-result docs, the DTO summaries and cmdlet help (reference regenerated), the public pages, the developer guide and the export execution flow diagram.

### What an administrator sees

- A pre-hire's start date arrives while their Initial Password is queued, retrying or parked: the export succeeds, the account stays disabled, the confirming import raises no "not confirmed" warning, and Enforce State and Sync Preview leave it alone.
- Once the password is delivered the account is enabled within seconds; the delivery's Activity says the enable was held back until the account had a password; the next import confirms `512`.
- If the password expires (the Connected System's initial password time to live, 7 days by default) or is cancelled, the next import stops waiting and the export reports today's actionable error.

## Implementation Phases

### Phase 1: Engine flag and contained lookup ✅

`FlagExportsFollowedByAPasswordAsync`, its call sites, the in-memory failure hook, and the doc on `PendingExport.InitialPasswordFollows`, `ConnectedSystemExportResult.AttributesAwaitingInitialPassword` and `ISyncRepository.GetMetaverseObjectIdsOwedPasswordAsync`.

### Phase 2: LDAP Connector resend ✅

`CanWithholdEnablementAfterRefusal`, `WithEnablementWithheld`, `FindEnablingUserAccountControl` and `TryWithholdEnablement` in `LdapConnectorExport`; `UpdateSucceeded` reports the awaiting attribute; the classifier's message.

### Phase 3: Delivery wait and write order ✅

`PasswordEnablementPolicy.WaitsForAnExportInFlight`, the postpone wording, and `BulkUpdatePendingExportsRawAsync`'s order.

### Phase 4: Tests, documentation and integration ✅

Unit tests (connector resend paths extending `LdapAccountEnableRefusalClassificationTests`, a stateful fake-Active-Directory composite through #2161's compare-and-swap, engine flag tests including the parallel path, the policy table, the delivery flips), a `RequiresPostgres` behavioural write-order test, public and engineering docs, the changelog, and Scenario 024's `EnableWhileOwed` step (Active Directory lab only).

## Success Criteria

- An enabling Update to an account owed a password, refused by Active Directory, exports successfully at `514`, is held, and is enabled by the delivery with the `CompletesWithheldEnable` basis; the next import confirms it.
- An Update to an account that has a password, or that is owed nothing, behaves exactly as before.
- Scenario 024's `EnableWhileOwed` step passes on the Windows Active Directory lab.

## Risks & Mitigations

- **Active Directory's wording on a modify is unconfirmed on a real domain controller.** The unit fixtures already include `0000052D` on an Update's modify, and enabling a password-less account failing with ERROR_PASSWORD_RESTRICTIONS is well-known Active Directory behaviour, but the lab has not answered it yet. If the code differs, the resend never fires and the behaviour is today's actionable failure. Scenario 024's `EnableWhileOwed` step is the check.
- **Person-keyed flag.** The flag, like the hold, is keyed on the Metaverse Object, so a person with two accounts of different object types on one Connected System may have the account with nothing queued held until the other's password is delivered, then released and refused as today. It converges.
- **Switched-off systems.** A propagated change held because the system's Password Synchronisation is switched off counts as owed, as it already does for the import hold: the account waits, disabled, until the system is switched on or the change expires.
- **Wider wait.** Deliveries to an account with an export in flight postpone 15 seconds more often, including on directories where no hold can arise. Bounded by #2083 and by the one-hour stranded-export ceiling.
- **Residual races (converge one cycle late).** A delivery whose probe ran before the batch was marked Executing and whose write lands after the owed lookup leaves `514` held, released by the next import. A delivery that sets the password and enables the account in the one round trip between the refused `512` and the `514` resend is overwritten by the resend; the hold is then released by the next import and the account enabled by the export after it. The resend is not made conditional, because the Connector does not know the value before the refusal.
- **Permissive domains.** Where the domain policy allows an account with no password, Active Directory accepts the `512`, so the account is enabled before its password lands: the same as before this change, and unlike #2039's guarantee for Creates. Option B's proactive signal layered on top would extend that guarantee to Updates if it is wanted; it is not built.
- **Bisection replay.** A refusal raised from one step of the fallback in parts propagates out of `SendModify`, and the resend replays the whole request, including steps already applied. Safe: every replayed step is a Replace, or a value change the directory accepts as already in place under Permissive Modify or answers with the same fallback.
- **Load.** One refused modify per held account, only on the enabling Update of an account still owed a password.
