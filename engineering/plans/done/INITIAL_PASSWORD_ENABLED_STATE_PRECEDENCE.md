# Initial Password Delivery Leaves the Enabled State to the Attribute Flows That Set It

- **Status:** Done
- **Note:** Verified on Samba AD (Scenario 001 at Medium with an Initial Password: the Archived joiner stays disabled, the confirming import confirms). The Windows Active Directory lab run is still to do, together with #2039's, once the lab is reachable.
- **Issue:** [#2064](https://github.com/TetronIO/JIM/issues/2064)
- **Created:** 2026-10-12

## Overview

When an Initial Password is delivered, JIM applies the provisioning Synchronisation Rule's **Enable Account** setting to the account with no regard for the Attribute Flows. The LDAP Connector clears `ACCOUNTDISABLE` (`0x2`) when Enable Account is on (`LdapConnectorPassword.ApplyUserAccountControlFlags`). An account the `userAccountControl` Attribute Flow deliberately creates disabled, such as a pre-hire or an archived joiner (`514`), is therefore enabled with a password JIM knows. It stays enabled until drift detection corrects it on a later synchronisation, and the confirming import reports `ExportNotConfirmed`.

This happens on Samba AD today. On real Active Directory it also blocks Scenario 001's Joiner on the lab, which the #2039 plan records as Open Decision 6.

The fix makes the precedence explicit:

- Where an export writes the attribute that governs whether the account is enabled (`userAccountControl` on Active Directory and Samba AD), that Attribute Flow decides, and delivering the password leaves the enabled state alone.
- The one exception is the #2039 case: JIM created the account disabled only because Active Directory needs a password first, so the delivery completes the enable the flow asked for.
- Where nothing writes the enabled state, Enable Account applies.
- An administrator's explicit set-password choice still wins.

## Business Value

- No window where a disabled-by-design account is usable with a password JIM knows. This matters because JIM is deployed in high-trust environments.
- The confirming import confirms what the flow asked for, so a correct configuration produces no `ExportNotConfirmed` noise.
- Unblocks the Active Directory lab run of Scenario 001 (#2039 Phase 3).
- Enable Account "off" now means "leave the account as it was created", which is what the portal help text already says it does.

## Decisions (agreed 2026-10-12)

1. **How JIM knows a flow sets the enabled state: from what was exported.** A Connector reports, per successful export, that it wrote the attribute through which the account is enabled or disabled. JIM records that on the account's owed Initial Password. The alternative, deriving it from the rule configuration, was rejected: a leaver-only rule mapping `userAccountControl` would have left every joiner of a provisioning rule without that flow disabled.
2. **Enable Account off means "leave as created"** (send `null`), not "disable" (send `false`). This is a behaviour change, so it gets a changelog entry. It makes no difference to a freshly created account, which Active Directory and Samba AD create disabled without a `userAccountControl` value. It stops a late delivery disabling an account a separate process has since enabled.
3. **Sibling defects are filed, not fixed here:**
   - [#2158](https://github.com/TetronIO/JIM/issues/2158): an enabling Update while a password is owed on real AD (blocked by this).
   - [#2159](https://github.com/TetronIO/JIM/issues/2159): `DONT_EXPIRE_PASSWORD` against a flow.
   - [#2160](https://github.com/TetronIO/JIM/issues/2160): a coalesced Initial Password loses Enable Account (blocked by this).
   - [#2161](https://github.com/TetronIO/JIM/issues/2161): the `userAccountControl` read-modify-write race.
   - [#2162](https://github.com/TetronIO/JIM/issues/2162): the undocumented `enableAccount: false` on the explicit set-password REST endpoints.

## Technical Architecture

### Current state (verified against source at 0a457cb52)

- **Staging.** `ExportExecutionServer.ProcessBatchSuccessAsync` collects each successful Create with a Connected System Object and a provisioning Synchronisation Rule. `StageInitialPasswordsForBatchAsync` then stages one Provisioned `PendingPasswordChange` per account, with no enable decision of its own.
- **Delivery.** `PasswordSynchronisationServer.DeliverProvisionedAsync` sends `PasswordSetOptions { EnableAccount = rule.EnableAccount }`, read fresh from the Synchronisation Rule. Explicit rows send the administrator's choice; Propagated rows send `null`.
- **Connector.** `LdapConnectorPassword.ApplyUserAccountControlFlags` clears `0x2` for `true`, sets it for `false`, and leaves it for `null`. It always applies the expiry bit; that is #2159.
- **The ambiguity.** The live directory value carries no provenance: on real AD, #2039's interim `514` and a `514` the flow chose look the same. The Connected System Object's value is no help either. Optimistic apply runs after staging and never records a held value, and after the confirming import every AD account has a `userAccountControl` value, whether a flow set it or the directory defaulted it.

### Proposed solution

**1. The export fact (the Connector observes; the engine decides).**
- New `bool SetsAccountEnabledState` on `ConnectedSystemExportResult`, meaning "this export wrote the attribute through which the Connected System enables or disables the account".
- The LDAP Connector reports it on the Active Directory family (ActiveDirectory and SambaAD) when:
  - a Create's add carries `userAccountControl`;
  - an Update's modify writes `userAccountControl`;
  - a Delete performed as a disable writes `userAccountControl`.
- OpenLDAP, 389 Directory Server and Generic never report it: Enable Account has no meaning there. Other Connectors are untouched.
- Invariant: a non-empty `AttributesAwaitingInitialPassword` implies `SetsAccountEnabledState`.

**2. The row field.**
- New persisted `bool EnabledStateSetByAttributeFlow` on `PendingPasswordChange`, meaningful for Provisioned rows.
- Migration: not null, default false.
- `PendingPasswordChangeBulkColumns` insert and supersede lists, both UPSERT writers in list order, `Supersede`, and the in-memory twin all carry it.
- Explicit and Propagated writes pass false.

**3. Stamping.**
- `StageInitialPasswordsForBatchAsync` sets the field from the Create's result.
- For a successful non-Create export that reports the fact, one narrow status-mark UPDATE marks the account's Provisioned row, whatever its status: `ISyncRepository.MarkOwedInitialPasswordsAsSetByAttributeFlowAsync`. A Cancelled row comes back to Pending on a Retry, so it is marked too. So a leaver disabled, or an account disabled by deprovisioning, while its password is still on the queue is never re-enabled by a late delivery.
- A failure is logged, counted and put on the export Activity as a warning; it never fails the export.

**4. The decision: one pure engine function.**

`PasswordEnablementPolicy.Decide` returns an `AccountEnablementDecision` (`bool? EnableAccount` and an `AccountEnablementBasis`). Rows are evaluated in order; the first match wins:

| # | Origin | Condition | Sends | Basis |
|---|---|---|---|---|
| 1 | Explicit | The administrator chose to enable or disable | That choice | AdministratorChoice |
| 2 | Any (Explicit only with no choice) | The account holds a withheld enable (#2039) | `true` | CompletesWithheldEnable |
| 3 | Provisioned | `EnabledStateSetByAttributeFlow` | `null` | LeftToAttributeFlow |
| 4 | Provisioned | Otherwise | Rule on: `true`; rule off: `null` | SyncRuleSetting |
| 5 | Propagated, or Explicit with no choice | Otherwise | `null` | LeftAsIs |

Both inputs are read afresh at every attempt, in one round trip (`ISyncRepository.GetAccountEnablementFactsAsync`), not taken from the claimed row: a lane claims a batch and works through it one account at a time, so an export can mark a claimed row before the lane reaches it. "Holds a withheld enable" is the partial-index probe behind `GetConnectedSystemObjectIdsAwaitingInitialPasswordAsync`. It is true only while the flow still asks for enabled and JIM is the only reason the account is disabled:
- a leaver's value supersedes it before the confirming import;
- `IsKeptAgainstAlreadyCurrent` withdraws it after the import.

A failed probe keeps the attempt as a transient failure; it never guesses.

**5. Visibility.** Where the precedence overrode what the setting alone would do, the delivery outcome Activity says so: "the account's enabled state was left to its Attribute Flow", or "the account enabled as its Attribute Flow asks". The lane summary counts both. That is visible on the portal, REST and PowerShell through the existing Activity views. No new setting or parameter, so surface parity needs text changes only.

### Scenario outcomes

| Scenario | Delivery sends | Outcome |
|---|---|---|
| Samba AD, flow `514` (the reported bug) | `null` | Stays `514` with a password; the import confirms; no enable window |
| Samba AD, flow `512` | `null` | Stays `512`; with Enable Account off it is no longer disabled by delivery |
| Real AD, flow `512` | `true` (held) | `514` to `512` within about a second of delivery; the import confirms |
| Real AD, flow `514` (Scenario 001 Archived joiner) | `null` | Stays `514`; the lab Joiner is unblocked |
| No `userAccountControl` flow (Scenarios 017, 020, 024) | Rule on: `true` | Enabled from the directory default, as today |
| Provisioning rule without the flow, a separate leaver rule with it | Joiner: `true` | The joiner is enabled; a leaver disabled while the password is owed is marked and never re-enabled |
| Leaver during the #2039 hold | `null` | The hold is superseded or withdrawn, so the account stays `514` |
| Explicit with a choice | The choice | Unchanged override |
| Parked, then Retry | Recomputed | Stamp and hold read afresh, rule read fresh |
| Cancelled, the person leaves, then Retry | `null` | The leaver's export marked the Cancelled row, so the retried delivery leaves the account disabled |
| Marked after the lane claimed the row | `null` | The mark is read at the attempt, not from the claim |
| OpenLDAP, 389, Generic | Rule on: `true`; off: `null` | No change: these directories ignore it, with the existing caveat |
| Rows staged before the upgrade | The rule's value | Default false keeps today's behaviour for rows retrying across the upgrade; no backfill |

## Implementation Phases

### Phase 1: The decision and the row field ✅

- Write the policy truth table test first (red), then `PasswordEnablementPolicy` and `AccountEnablementDecision`.
- `PendingPasswordChange.EnabledStateSetByAttributeFlow`, `Supersede`, migration, bulk columns, UPSERT writers, and the in-memory twin.
- `BulkInsertColumnCompletenessTests`, and a `RequiresPostgres` round trip through both UPSERTs and the mark UPDATE.

### Phase 2: The export fact and stamping ✅

- LDAP Connector: Create, Update and Delete-as-Disable report `SetsAccountEnabledState` on the AD family, on both the sync and async paths.
- `ExportExecutionServer`: the stamp at staging, and the mark for non-Create exports.

### Phase 3: Delivery ✅

- `DeliverProvisionedAsync` and the Explicit/Propagated branch build `EnableAccount` from the policy, with the hold probe and the basis on the Activity.

### Phase 4: Surfaces, documentation and integration ✅

- Help text on the portal, REST and PowerShell.
- Public docs rewritten in place (MNT-1).
- `engineering/INTEGRATION_TESTING.md`, and a CHANGELOG fix entry plus a changed-behaviour entry for "off".
- Scenario 001 with `-InitialPassword` on Samba AD as well as Active Directory, asserting the Archived joiner keeps `0x2` after delivery, with its Pester test.

## Success Criteria

- On Samba AD, Scenario 001's Joiner with an Initial Password leaves the Archived joiner disabled and reports no `ExportNotConfirmed`; Tests 2d and 2e still toggle the enabled state from HR data.
- Scenarios 017, 020 and 024 (no `userAccountControl` flow) still enable accounts through the Initial Password.
- The policy is one unit-tested truth table, and every delivery the precedence decided differently from the setting says why on its Activity.

## Risks & Mitigations

- **Residual race, two windows.** A leaver whose disabling export commits between a delivery attempt's read and the Connector's write can still see a held enable completed, or an unmarked row enabled. Separately, the mark for an export other than the Create is recorded only once that export's whole batch has returned from the Connector and its results are processed, so a delivery attempt during the rest of the batch reads the row unmarked; that window is seconds on a large batch, not milliseconds. Either way the next import reports the flow's value as not confirmed and the export after it disables the account. Closing the second window (deferring a delivery while its account has an export in flight, and recording the mark before the export's status settles) is left for a follow-up, [#2173](https://github.com/TetronIO/JIM/issues/2173). The broader read-modify-write race is #2161.
- **Upgrade window, accepted.** Rows staged before the migration default to unmarked. JIM is pre-release with no deployments to carry across, so there is no backfill and no customer guidance.
- **Real AD, `514` to `512` while the password is Parked.** The enabling Update is refused with Password Required and retried; it fails closed and is actionable. Holding it is #2158.
- **Unverified on the lab, inherited from #2039:** whether Active Directory stores `514` or `546` for an explicit `514` create. Check it on the first lab run.
