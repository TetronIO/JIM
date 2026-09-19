# Initial Passwords on the Password Delivery Service: Implementation Plan

- **Status:** Doing (Layer 1 complete; Layer 2 docs and changelog delivered, origin-on-surfaces and Connected System removals outstanding)
- **Issue:** [#1697](https://github.com/TetronIO/JIM/issues/1697)
- **PRD:** [PRD_PASSWORD_SYNCHRONISATION.md](../../prd/doing/PRD_PASSWORD_SYNCHRONISATION.md)
- **Created:** 2026-09-19

## Overview

An account JIM provisions by export used to get its first password from a separate pass at the end of the export run that created it (#1121: `InitialPasswordDeliveryServer`, table `PendingInitialPasswords`), with the next export run as the only retry. User-set and propagated passwords already travel the Password Delivery Service (#1635): queued row, database wake-up, delivery in under a second, retries on backoff, outcomes on Operations > Passwords and on the identity's password timeline. [Convergence decision D7](../done/PASSWORD_PIPELINE_CONVERGENCE.md#resolved-design-decisions) left initial passwords out of that work; this feature brings them onto the same queue and retires the second store outright, JIM being pre-release with no in-flight rows to carry across.

Delivered as a two-layer stacked pull request:

| Layer | Branch | Delivers |
|---|---|---|
| 1 | `feature/initial-passwords-on-delivery-service` | The model change (a `Provisioned` origin, nullable `SyncRuleId` and `EncryptedPassword`), export-time staging onto the queue, delivery-lane handling of provisioned rows, Synchronisation Rule-side reads and release moved onto the queue, removal of the old store, the export-run pass and the retention setting, the migration, and RequiresPostgres coverage. |
| 2 | `feature/initial-passwords-on-delivery-service-stack-surfaces` | The origin surfaced on the queue, the timeline and PowerShell; the Connected System's own initial-password indicator and REST counts removed; docs, changelog and the engineering plan; Scenario 17 updated to poll the queue instead of the export Activity. |

## Business Value

- A newly provisioned account has its first password within a second or two of existing, rather than waiting for a pass appended to the end of its export run; a directory that was briefly unreachable is retried on the Connected System's own schedule instead of on the next scheduled export.
- One queue, one delivery service, one set of outcome states, one place to look. An initial password now shares Operations > Passwords, the identity's Password panel and the Password Synchronisation retention period with every other password change, instead of keeping its own store, its own retention setting and its own export-run step.
- A rule whose Initial Password settings were corrected releases its parked accounts to the Password Delivery Service directly; there is no need to wait for, or schedule, another export run to see the fix take effect.

## Resolved design decisions

Settled before implementation began (2026-09-19); the plan below implements them rather than revisiting them.

| # | Decision | Resolution |
|---|---|---|
| D1 | How a provisioned row carries its password | It does not. A new `PendingPasswordChangeOrigin.Provisioned` value; the row gains a nullable `SyncRuleId` (FK to `SyncRules`, `SetNull` on delete, filtered index) and `EncryptedPassword` becomes nullable, staying null for this origin. The password is resolved at each delivery attempt from the rule's `SyncRuleInitialPassword` settings (a stored static password, decrypted; otherwise generated from the custom or discovered policy) by `InitialPasswordResolver`, carrying over the decision logic of the retired `InitialPasswordDeliveryService` unchanged. |
| D2 | Where staging happens | Unchanged position in `ExportExecutionServer.ProcessBatchSuccessAsync`, immediately after the Connected System Object gets its external id. One queue row plus one parent Activity per inserted row is written (`ActivityTargetType.PasswordSynchronisation`, `SetPassword`, target context "Provisioned", system-initiated, recorded Complete as "Initial password queued for delivery to {system}.") through a new bulk `ISyncRepository.CreateActivitiesAsync`. Expiry behaviour and the enable-account setting are read from the rule at delivery time, never stamped onto the row at staging. |
| D3 | Coalescing on (Metaverse Object, Connected System) | Staging is one `INSERT ... ON CONFLICT DO UPDATE ... WHERE existing.Origin = Provisioned OR existing.Status IN (Expired, Cancelled)`. A live Explicit or Propagated row (Pending, Delivering, Parked) wins outright and no Activity is written for the provisioned attempt; an existing Pending row of either kind is made due immediately. An existing Provisioned row (the account was deleted and re-provisioned) is superseded by the new one; a later user-set or propagated password still supersedes a provisioned row through the pre-existing upsert path. |
| D4 | Delivery rules for provisioned rows | Delivered regardless of whether the Connected System has any Password Synchronisation configuration at all: every place that used to filter to `Origin != Propagated` under the name `explicitOnly` now reads `excludePropagated`, widened to admit `Provisioned` rows too. An account or Synchronisation Rule that has since gone is **withdrawn**: the row is removed and a completed child Activity records why, rather than the row retrying forever against nothing. A static password that cannot be decrypted, or a generator policy that cannot be satisfied, parks as a configuration fault; a policy rejection or an unsupported operation parks; everything else retries on the Connected System's own Password Synchronisation backoff and attempt limit, or JIM's transient defaults (five attempts, backing off from five minutes) where the system carries no configuration at all. |
| D5 | Release on rule save | `ReleaseParkedProvisionedPasswordChangesAsync(syncRuleId)` un-parks a rule's Provisioned rows over the same column set the existing `ReleasePasswordChangesForDeliveryAsync` uses; the database trigger's `NOTIFY` wakes the Password Delivery Service immediately, so a corrected rule is retried within seconds rather than on the Connected System's next export run. |
| D6 | Attention surfaces | The Synchronisation Rule keeps its parked/expired counts and grouped parked reasons, now sourced from the queue by origin and `SyncRuleId` rather than from the retired store. The Connected System's own indicator on the list, and the two initial-password counts on `ConnectedSystemDto`, are removed: the Password Synchronisation attention already on that list covers every origin, including Provisioned. |
| D7 | What is removed outright | `PendingInitialPassword` and its enums and table, the delivery service and result types built for it, the ten repository methods and bulk columns built only for it, `ISyncServer.DeliverOutstandingInitialPasswordsAsync` and the export run's dedicated phase, and the `History.InitialPasswordRetentionPeriod` setting and its own cleanup path (queue retention, a year by default, covers these rows the same way it covers every other finished password change). `InitialPasswordDeliveryServer` is renamed `InitialPasswordServer`, keeping only assessment, protection, release and rule-side attention; `JimApplication.InitialPasswords` keeps its name so no call site outside the renamed type needs to change. |
| D8 | Surfaces gain the origin | A single `PendingPasswordChangeDisplay.OriginLabel` drives **Set** / **Propagated** / **Initial** wherever the queue is shown: the Operations tab, the queue DTO (`Origin`, `SyncRuleId`), the identity's timeline chip, and PowerShell's queue output. |
| D9 | Delete guard | `DeletePasswordChangesAsync` gains a `Status IN (Delivering, Cancelled)` guard, closing a pre-existing hole (widened here by withdrawal) where a row superseded mid-flight could be deleted by the delivery attempt that was already in progress against it. |
| D10 | Delivery shape | Two-layer stack as set out in Overview above. The migration is generated once, in the middle of Layer 1, after the old entity is removed from the model; the packages either side of it are deliberately additive so the solution compiles throughout, and RequiresPostgres coverage runs only once the migration exists. |

## Technical Architecture

### Current state (before this feature)

- A Create export that succeeds calls `ExportExecutionServer.StageInitialPasswordsForBatchAsync`, which writes a `PendingInitialPassword` row: a durable, per-Connected-System-Object debt, carrying no password value, in its own table.
- `InitialPasswordDeliveryServer` (`JimApplication.InitialPasswords`) runs a delivery pass once at the end of every export run, over every `PendingInitialPassword` row the Connected System still owes, not only the ones the run just created. `InitialPasswordDeliveryService` (persistence-free) decides what to send and classifies the result; the server records it.
- `PendingInitialPasswordStatus` has three states: `Pending`, `Parked`, `Expired`. A delivered row is deleted outright, because JIM keeps no record of a password beyond the Activity.
- Retention is a dedicated setting, `History.InitialPasswordRetentionPeriod` (90 days by default), trimmed on the same History Retention Cleanup Schedule as everything else, but as its own pass with its own counts.
- The Connected Systems list carries its own parked/expired chip, and `ConnectedSystemDto` carries `ParkedInitialPasswordCount` / `ExpiredInitialPasswordCount`, both sourced from the old store.
- User-set and propagated passwords already travel `PendingPasswordChange` (#1635): one row per (Metaverse Object, Connected System), delivered by the Password Delivery Service, woken by a database `NOTIFY` trigger, claimed with `FOR UPDATE SKIP LOCKED`, retried on a doubling backoff, parked on a refusal.

### Layer 1: model, staging, delivery, rule reads, removal (✅ complete)

**Model.** `PendingPasswordChangeOrigin.Provisioned = 2` added beside `Explicit` and `Propagated`. `PendingPasswordChange` gains `int? SyncRuleId` (FK `SetNull`, filtered index `IX_PendingPasswordChanges_SyncRuleId`) and `EncryptedPassword` becomes `string?`; `Supersede` carries `SyncRuleId` across a coalesce. `PendingPasswordChangeHeader`'s `IsDue` reads `Origin != Propagated` so a Provisioned row on a paused Connected System is still due, never held.

**Resolution.** `InitialPasswordResolver` (`JIM.Application/Services`) decides what password a rule's Initial Password settings resolve to, or why none can be sent, without touching a Connector or the database: a static password is decrypted through `ICredentialProtection`; otherwise one is generated from the rule's custom policy or the Connected System's discovered one. No password value crosses its boundary except a genuinely usable resolution's own value; a refusal carries only a message and a failure reason.

**Staging.** `ExportExecutionServer`'s staging path (still named `StageInitialPasswordsForBatchAsync`, its behaviour rewritten under D1 to D3) calls `ISyncRepository.StageProvisionedPasswordChangesAsync`, a single `SqlQueryRaw<Guid>` implementing the coalescing `INSERT ... ON CONFLICT` above, and `CreateActivitiesAsync` for the parent Activities on inserted and superseded rows. A `null` `MetaverseObjectId` on the batch counts as a staging failure, as it always has.

**Delivery.** `PasswordSynchronisationServer.DeliverOneAsync` returns a `PasswordDeliveryDisposition` (`Delivered`, `Kept`, `Withdrawn`); the provisioned branch reads the row's `SyncRuleId` configuration once per claimed batch (the discovered policy once per lane), asks `InitialPasswordResolver`, and applies D4's classification. `PasswordDeliveryRunResult` and `PasswordDeliveryPassResult` gain `WithdrawnCount`, surfaced in the pass's summary log. `explicitOnly` is renamed `excludePropagated` everywhere it appears (claim, expire, the due-Connected-System-ids read, the queue outlook and summary reads, the header's `IsDue`, the in-memory repository, and `DescribeRow`'s Held case), widening every one of those reads to admit Provisioned rows. `DeletePasswordChangesAsync` gains the `Status IN (Delivering, Cancelled)` guard from D9.

**Rule-side reads and release.** `InitialPasswordDeliveryServer` is renamed `InitialPasswordServer`, keeping only configuration assessment and protection, `ReleaseParkedProvisionedPasswordChangesAsync`, `GetProvisionedPasswordAttentionBySyncRuleAsync` and `GetParkedProvisionedPasswordReasonsAsync`; every rule-facing surface (`ConnectedSystemServer`, `SynchronisationController`, `SyncRuleList.razor`, `SyncRuleDetail.razor`, `SyncRuleInitialPasswordTab.razor`) calls the same members under their old names and compiles unchanged.

**Removal and migration.** `PendingInitialPassword.cs`, its enums, `InitialPasswordRunResult`, `InitialPasswordDeliveryResult`, `InitialPasswordDeliveryService`, its bulk columns, and the tests built only for them are deleted outright, along with the ten repository methods that existed only to serve the old store, `ISyncServer.DeliverOutstandingInitialPasswordsAsync`, the export run's dedicated phase (`RunPhaseKeys.ExportDeliverInitialPasswords` and its catalogue entry and icon), and `History.InitialPasswordRetentionPeriod` end to end (Constants, `ServiceSettingsServer`, `SeedingServer`, the configuration change classifier, history retention cutoffs, `ChangeHistoryServer`, the Worker's summary log, the history REST controller and its DTOs). Migration `20260919095623_ProvisionedPasswordsOnTheDeliveryQueue` drops the `PendingInitialPasswords` table, makes `EncryptedPassword` nullable, adds `SyncRuleId` with its index and foreign key, and deletes the `History.InitialPasswordRetentionPeriod` row from `ServiceSettingItems` by hand. `dotnet ef migrations has-pending-model-changes` reports none; `MigrationDesignerChainTests` and `ReleasedMigrationImmutabilityTests` are green.

**Tests.** RequiresPostgres coverage added after the migration: claim, expire, due-Connected-System and outlook/summary reads over a paused system; the delete guard both ways; `SyncRuleId` and a null password round-tripping; the staging conflict matrix (a pending propagated row made due, a parked one left alone, an expired one superseded, a provisioned one superseded); a Synchronisation Rule delete nulling `SyncRuleId`; `CreateActivitiesAsync`; release scoped to one rule only; attention counts and grouped parked reasons.

### Layer 2: surfaces, removals, docs (this work)

**Origin on the surfaces (outstanding).** `ActivitiesRepository`'s CASE arm gains a "Provisioned" branch; `PendingPasswordChangeDisplay.OriginLabel` drives **Set** / **Propagated** / **Initial** on `MetaverseObjectPasswordPanel.razor`'s timeline chip; `PendingPasswordChangeDto` gains `Origin` and `SyncRuleId`; the Operations Passwords tab shows the origin per row; `Get-JIMPendingPasswordChange` and its docs gain the field.

**Connected System surfaces removed (outstanding).** `ConnectedSystemList.razor`'s indicator and its load, and `ConnectedSystemDto`'s two counts and the parameter that populated them, are removed; `SynchronisationController`'s fold of those counts goes with them. The Synchronisation Rule's Initial Password tab wording changes to name the Password Delivery Service and "within seconds" rather than the next export run.

**Docs, changelog, this plan (delivered here).** `docs/concepts/passwords.md`'s "Giving new accounts their first password" section rewritten for the queue-based delivery model, its five-state table (Delivered / Retrying / Parked / Withdrawn / Expired), and the diagram (`docs/assets/diagrams/initial-password.svg`) and its caption pointed at the Password Delivery Service rather than an end-of-run pass; `docs/configuration/synchronisation-rules.md`'s Initial Password section rewritten to match; `docs/configuration/operations.md`, `docs/administration/configuration.md`, `docs/powershell/history.md` and `docs/powershell/synchronisation-rules.md` updated for the retired setting and the new delivery wording; `CHANGELOG.md` gains a 🔄 entry for the behaviour change and a 🗑️ entry for what was removed, and two now-inaccurate Unreleased entries from earlier in the same feature's history (next-export-run wording, and the Connected Systems list chip) are corrected in place since nothing describing them has shipped yet; this plan document filed as Doing.

**Scenario 17 (outstanding).** Updated to poll `Get-JIMPendingPasswordChange` for the provisioned account rather than reading the export Activity's own outcome, matching where the work now actually completes.

## Deviations

Recorded during Layer 1 implementation (2026-09-19):

- **`PendingInitialPassword.DefaultTimeToLive` moved to `PendingPasswordChange.DefaultTimeToLive` rather than being deleted with the rest of the old model.** Two live call sites (`ConnectedSystem`'s effective time-to-live, and `ConnectedSystemPasswordSynchronisation`'s expiry cap) read the seven-day default from it, and both needed a constant to keep reading from once the old type was gone. Moving it, rather than duplicating the value on the new type, keeps one definition of "seven days" for both purposes it always served.
- **The Connected System list indicator and `ConnectedSystemDto`'s two counts were removed in Layer 1, not Layer 2 as originally planned.** Removing the ten repository methods built only for the old store included the one those counts were read through; leaving the counts in place would have left `ConnectedSystemServer` and `SynchronisationController` calling a method that no longer existed, so the removal moved earlier than the surfaces work it was originally grouped with. Layer 2's docs and changelog already describe the counts as gone, on schedule.

## Success Criteria

- A Synchronisation Rule with Initial Password switched on delivers a new account's password within a second or two of the export creating it, without a second export run, verified by an integration test and by the runtime check below.
- No `PendingInitialPasswords` table, and no `History.InitialPasswordRetentionPeriod` setting, exist after the migration; `dotnet ef migrations has-pending-model-changes` reports none.
- An initial password appears on Operations > Passwords and the identity's Password panel with origin **Initial**, and nowhere else carries a separate initial-password queue view.
- A Synchronisation Rule's Initial Password tab still reports parked and expired counts and grouped parked reasons, now sourced from the queue.
- `dotnet build JIM.sln` and `dotnet test JIM.sln` are clean; RequiresPostgres coverage is green; Scenario 17 passes with its Scenario 20 sibling unaffected.

## Dependencies

None new. Builds entirely on the Password Delivery Service, the `PendingPasswordChange` queue and the database `NOTIFY` trigger delivered under #1635.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| An account deleted and re-provisioned before its first delivery attempt | Staging supersedes an existing Provisioned row for the same (Metaverse Object, Connected System) pair rather than leaving two rows to race. |
| A parked Propagated row on the same account and system blocks the initial password from ever being tried | Accepted: it is already an attention item in its own right, and the person's real password is the better one to land once it is dealt with. |
| A multi-domain-controller directory answers "not found" on the very first attempt, seconds after the account was created, before replication catches up | Retried on the Connected System's own backoff (five minutes by default) like any other transient failure; still a large improvement over waiting for the next export run, and called out explicitly in the docs. |
| `ReleasePasswordChangesForDeliveryAsync(csId)` also un-parks Provisioned rows parked for a rule fault it cannot fix | They simply re-park on the next attempt; a comment notes this rather than adding special-case handling for a rare, self-correcting overlap. |
| A CodeQL name-heuristic flag on new identifiers containing "Password" | Answered by naming the actual data flow in review; identifiers are never renamed just to dodge the heuristic. |
| Migration timestamp ordering against `released-migrations.lock` | Generated once, after the old entity was fully removed from the model, and re-checked if `main` gains a migration first. |
| Constructor churn on `PasswordSynchronisationServer` across six call sites, and the `InitialPasswordDeliveryServer` to `InitialPasswordServer` rename | Both are mechanical (a type rename, and two added constructor parameters); no call site changes behaviour. |
