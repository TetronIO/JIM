# Password Delivery Leaves "Password Never Expires" to Whoever Owns userAccountControl

- **Status:** Done
- **Note:** Runtime verification on Samba AD (Scenario 001 with `-InitialPasswordExpiry NeverExpires`, Scenario 020 Test 6b, the sign-in check) and the Active Directory lab hold path are still to run.
- **Issue:** [#2159](https://github.com/TetronIO/JIM/issues/2159)
- **Created:** 2026-10-13

## Overview

On Active Directory and Samba AD, "Password never expires" is the DONT_EXPIRE_PASSWORD bit (`0x10000`) of `userAccountControl`, the same attribute that carries the enabled state (`0x2`). The LDAP Connector used to set that bit for an expiry of Never expires and clear it for the other two, on every delivery, with no regard for who owns the attribute:

- An Attribute Flow asserting `66048` (enabled, never expires) with an Initial Password of "Require a change" or "Expires by policy" had `512` written over it. On Samba the confirming import then reported `ExportNotConfirmed`; on Active Directory the flow's value was re-exported, or Enforce State reasserted it, and Require change then did nothing (pwdLastSet = 0 forces a change only without DONT_EXPIRE_PASSWORD).
- Every propagated password cleared the bit, whoever had set it, and every administrator reset that did not choose Never expires did too.

#2064 had already decided the enabled state with an ownership ladder. This plan extends that one ladder to both flags, so a delivery writes neither where something else owns `userAccountControl`.

## The rule

"Password never expires" belongs to whatever owns `userAccountControl`. "Must change at next sign-in" belongs to the password, so pwdLastSet = 0 is always written for Require change, after the password and after the flags.

One decision, `AccountFlagsPolicy.Decide` (`JIM.Application/Services`, the renamed `PasswordEnablementPolicy`), returns an `AccountFlagsDecision` with a value and a basis (`AccountFlagBasis`, the renamed `AccountEnablementBasis`; neither was persisted or exposed) per flag. First match wins:

| # | Origin | Condition | Enabled (`0x2`) | Password never expires (`0x10000`) |
|---|--------|-----------|-----------------|------------------------------------|
| 1 | Explicit | (any) | The administrator's enable choice, where made | Set for Never expires, cleared otherwise (AdministratorChoice) |
| 2 | Any | Account holds a withheld enable (#2039) | Enabled (CompletesWithheldEnable) | Not affected: the next rung for the origin decides |
| 3 | Provisioned | An export wrote `userAccountControl` (the #2064 mark) | Left (LeftToAttributeFlow) | Left (LeftToAttributeFlow) |
| 4 | Provisioned | Nothing wrote it | Rule's Enable Account: on enables, off leaves (SyncRuleSetting) | Rule's expiry sets or clears (SyncRuleSetting) |
| 5 | Propagated | (any) | Left (LeftAsIs) | Left (LeftAsIs) |

## Decisions (agreed 2026-10-13)

1. **One shared decision, not two parallel policies.** The enabled-state ladder of #2064 is evolved into a single decision for both flags; the table above is tested as one table (`AccountFlagsPolicyTests`).
2. **Keep the persisted column name.** `PendingPasswordChange.EnabledStateSetByAttributeFlow` keeps its name (renaming it is churn with a migration); its docs say it now means "an export wrote the account's flags", and the policy calls the parameter `accountFlagsWrittenByExport`. A Connector keeping the two flags in separate attributes would need a second fact.
3. **Connector boundary: a switch that cannot contradict the tri-state.** `PasswordSetOptions.LeavePasswordNeverExpiresAsIs` (default false, so every existing caller keeps its meaning) rather than a `bool? PasswordNeverExpires` beside `ExpiryBehaviour`. The engine sets it wherever the decision leaves the flag. A delivery that then needs no change sends no `userAccountControl` modify at all, so the #2161 compare-and-swap is never reached for it.
4. **The Connector reports what the account now does.** `AppliedExpiryBehaviour` is derived from the final `userAccountControl`. Require change with the flag left set returns a success with an expiry warning, which completes the delivery Activity with a warning (the bottom layer's plumbing). A flag left to its owner that merely differs from the request is information on the Activity, not a warning. `PasswordNeverExpiresChanged` says whether the flag changed.
5. **Propagated Never expires is refused in the core.** `PasswordSynchronisationServer.SetPasswordAsync` refuses `Targets == null` with `NeverExpires`, beside the #2162 disable refusal, so REST answers 400 through its existing handling. `Set-JIMMetaverseObjectPassword` checks first only so `-Generate` does not hand back a password for a refused request. Rows already queued with Propagated and Never expires are delivered with the flag left, and say so.
6. **JIM's own write of the flag waits for an export in flight (#2173).** `WaitsForAnExportInFlight` adds "Password never expires decided by the rule's setting" to "enable on JIM's own basis". #2158 widens the enable clause separately; the two clauses are kept apart so they merge cleanly.
7. **An explicit reset that an export will undo warns.** Where an explicit delivery changed the flag and either the account still held a #2039 enable (whose held export carries the flow's `userAccountControl`) or an export Synchronisation Rule with Enforce State writes `userAccountControl` for the account's Object Type (`AccountControlAttributeFlows.EnforcesAccountControl`, rules read once per batch and only when needed), the Activity completes with a warning naming the rule.
8. **Wording names the export, not a flow.** The #2064 mark is also set by a Delete performed as a disable, so the information sentence says the flag was "left to the export that writes userAccountControl".
9. **Portal text (approved).** A note on a `userAccountControl` export Attribute Flow where passwords are delivered to the rule's accounts (an icon with a tooltip in the virtualised table, a caption on the card); a caption and a conditional warning under the Initial Password's **After the password is set**; one line in the Set Password dialog. Conditions live in `AccountControlFlowNotes` (JIM.Web) and are tested.

## Implementation Phases

### Phase 1: Surface Connector expiry warnings (bottom layer) ✅

`completeActivityWithWarning`, `PasswordDeliveryAttempt`, `ExpiryBehaviourNotHonouredCount`, `PasswordChangeTargetOutcome.CompletedWithWarning`, and the dialog, timeline, REST and PowerShell surfacing.

### Phase 2: One decision for both flags ✅

- `AccountFlagsPolicy`, `AccountFlagsDecision`, `AccountFlagBasis`; table-driven tests over both flags and the wait.
- `PasswordSetOptions.LeavePasswordNeverExpiresAsIs`; `PasswordSetResult.PasswordNeverExpiresChanged`; LDAP Connector honours the switch, reports the applied expiry and warns on Require change defeated by the flag.
- Delivery wiring, counters (`PasswordNeverExpiresLeftToAttributeFlowCount`), Activity sentences (including the Static Initial Password's shared-password consequence), the Enforce State and held-export warnings, the postpone wording.
- Core refusal of propagated Never expires; controller docs; cmdlet pre-check and help.

### Phase 3: Surfaces and documentation ✅

Portal text, DTO summaries, cmdlet help and the generated reference, public docs rewritten in place (LDAP Connector Passwords leads with the read-modify-write recipe `SetBit(EnableUser(Coalesce(cs["userAccountControl"], 512)), 65536)`), integration script caveats, Scenario 020 Test 6b, changelog.

## Verification

- Unit: `AccountFlagsPolicyTests`, `LdapConnectorPasswordTests` (the no-modify reproducer, the Require change warning, `66050` to `66048`, contacts, changed-flag reporting, generic directories), `PasswordDeliveryTests` (left to the flow, rule's expiry whole, marked after the claim, propagated, held enable, explicit, queued propagated Never expires, information sentences, Static warning, Enforce State and held-export warnings, the wait), `SetPasswordRequestTests`, `MetaverseControllerSetPasswordTests`, `AccountControlFlowNotesTests`, Pester.
- Runtime on Samba AD (to run): Scenario 001 Medium with `-InitialPassword -InitialPasswordExpiry NeverExpires` (accounts stay `512`/`514`, no `ExportNotConfirmed`); a `66048` flow with Require change (`66048` kept, pwdLastSet 0, warning; a bind as the account succeeds because Samba, like Active Directory, ignores pwdLastSet 0 under DONT_EXPIRE_PASSWORD); Scenario 020 Test 6b; Scenario 017.
- Active Directory lab (to run): the #2039 hold path, `66050` becoming `66048`.

## Risks

- **Initial Password with a flow.** Never expires no longer applies to an account whose `userAccountControl` an export writes, including read-modify-write flows that would have carried it; the docs lead with the recipe and the Activity says so per delivery.
- **REST and PowerShell.** Never expires without named accounts is a 400.
- **More waiting.** Unmarked Provisioned rows now wait for an export in flight even with Enable Account off, bounded by `ExportInFlightCeiling`.
- **#2160.** A coalesced Initial Password becomes a propagated row and so loses the rule's expiry decision; #2160's fix should carry it with Enable Account.
