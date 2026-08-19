# Password Synchronisation (Phase 1: JIM as Password Origin)

- **Status:** Planned
- **Issue:** [#1119](https://github.com/TetronIO/JIM/issues/1119)
- **PRD:** [`engineering/prd/doing/PRD_PASSWORD_SYNCHRONISATION.md`](../prd/doing/PRD_PASSWORD_SYNCHRONISATION.md)

## Overview

This plan delivers Phase 1 of the Password Synchronisation PRD: per-Connected-System configuration, the encrypted durable queue, fan-out at the Metaverse, coalescing, time-to-live expiry, retry with backoff, drain-on-enable, the queue page, Activities under a new category, the Metaverse Object panel, the Connected System list indicator, Schedule-driven retention, and parity across portal, REST API, and PowerShell.

The shared foundation is already on `main` (see the PRD's Implementation Progress section): `IConnectorPasswordManagement` and the LDAP implementation, the credential attribute denylist, `PasswordSetFailureReason` classification, the `PendingInitialPassword` work store, `CredentialProtectionService`, `ConnectedSystem.InitialPasswordTimeToLive`, and set-password on all three surfaces. This plan builds only what is missing, and where the initial-password work already settled a design question (delete-on-success, Pending/Parked/Expired, recorded expiry, release-on-configuration-change), the queue adopts that answer rather than re-deriving it.

Phase 2 (inbound capture: the ingress API and inbound password mapping on import) is out of scope here and gets its own plan when Phase 1 lands.

## Business Value

Administrators currently have manual set-password (per account or per identity, synchronously) but no propagation: a password change reaches one system only, immediately, or not at all. This feature makes a password change durable and universal: it fans out to every enabled system, survives outages with visible retry, never silently drops, and leaves a complete audit trail without the value. It is the headline capability operators migrating from traditional ILM systems expect.

## Technical Architecture

### Current state

- `PendingInitialPassword` (`src/JIM.Models/Transactional/`) is a durable work list keyed uniquely on Connected System Object: no value stored, `Pending`/`Parked`/`Expired` states, delete-on-success, expiry recorded not swept, released by configuration change. Delivery is a pass appended to export runs (`SyncExportTaskProcessor.DeliverOutstandingInitialPasswordsAsync`); retry cadence is "the next export run", with no time-based backoff.
- `CredentialProtectionService` (`src/JIM.Application/Services/`) protects under the single purpose `JIM.Credentials.v1`, key ring shared across web, worker, and scheduler via `DataProtectionHelper` (`ApplicationName = "JIM"`).
- Set-password exists synchronously: `ConnectedSystemServer.SetConnectedSystemObjectPasswordAsync` / `SetPasswordOnAccountsAsync` (the latter already creates a parent Activity with per-account child outcomes: the fan-out Activity precedent), the shared `SetPasswordDialog.razor`, `POST .../connector-space/{csoId}/password`, and the two PowerShell cmdlets. There is no Metaverse-Object-level REST endpoint; PowerShell loops the per-account endpoint client-side.
- Retention trims run in worker housekeeping (`Worker.PerformChangeHistoryCleanupAsync`, 6-hourly), not on a Schedule. #1118 (Schedule-driven cleanup) is unstarted.
- Worker tasks are table-per-type `WorkerTask` subclasses dispatched by a `switch` in `Worker.ExecuteAsync`; `ConfigurationChangePreviewTaskProcessor` is the pattern for a small static processor outside the sync-run family.

### Proposed solution

**One new queue entity, one new server, one new worker task type, one new Schedule step type.**

```
Administrator / API / PowerShell / (future Phase 2 ingress)
        │  password change for a Metaverse Object
        ▼
PasswordSynchronisationServer.QueuePasswordChangeAsync
        │  parent Activity created; per enabled target system:
        │  UPSERT PendingPasswordChange (coalesce on MVO + CS)
        ▼
PendingPasswordChange queue (payload encrypted, JIM.PasswordSync.v1)
        │  PasswordDeliveryWorkerTask (enqueued on change, on enable,
        │  on manual retry; housekeeping enqueues when retries fall due)
        ▼
PasswordDeliveryTaskProcessor → IConnectorPasswordManagement.SetPasswordAsync
        │  success: delete row, child Activity records outcome
        │  transient failure: AttemptCount++, NextRetryAt = backoff
        │  policy rejection / unsupported: Parked (manual retry only)
        │  past ExpiresAt: Expired, recorded outcome
```

#### The queue entity: `PendingPasswordChange`

`src/JIM.Models/Transactional/PendingPasswordChange.cs`, following `PendingInitialPassword`'s shape with the three genuinely new pieces (value, coalescing, time-based retry):

| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `MetaverseObjectId` | `Guid` | fan-out source identity |
| `ConnectedSystemId` | `int` | denormalised, as `PendingInitialPassword` does |
| `ConnectedSystemObjectId` | `Guid?` | null when the target account does not exist yet (Resolved Decision 2: queue, bounded by TTL) |
| `EncryptedPassword` | `string` | the only persisted value, protected under the dedicated purpose |
| `ExpiryBehaviour` | `PasswordExpiryBehaviour?` | carried from the change request |
| `Status` | `PendingPasswordChangeStatus` | `Pending` / `Parked` / `Expired`; no delivered state, success deletes the row |
| `FailureReason` | `PasswordSetFailureReason?` | reuse; drives retry-vs-park exactly as `InitialPasswordDeliveryService.Classify` does |
| `TargetMessage` | `string?` | target's verbatim reason, already password-stripped by the Connector |
| `AttemptCount` | `int` | |
| `NextRetryAt` | `DateTime?` | **new versus the initial-password store**: exponential backoff, because delivery is not tied to an export run |
| `CreatedAt` / `LastAttemptedAt` / `ExpiresAt` | `DateTime` / `DateTime?` / `DateTime` | `ExpiresAt = CreatedAt + ConnectedSystem.EffectiveInitialPasswordTimeToLive` (divergence 5: adopt the existing TTL, do not grow a second one) |
| `ActivityId` | `Guid` | the parent Activity that created the row (requirement 27) |

EF configuration mirrors `PendingInitialPassword`: cascade delete from Metaverse Object and Connected System; **unique index on (`MetaverseObjectId`, `ConnectedSystemId`)**, which is what makes coalescing an `INSERT ... ON CONFLICT ... DO UPDATE` (newer password, reset attempts, new `ExpiresAt`, new `ActivityId`) rather than application-side read-modify-write; a (`ConnectedSystemId`, `Status`, `NextRetryAt`) index for the delivery query and the indicator counts. A `PendingPasswordChangeBulkColumns` constants file guarded by `BulkInsertColumnCompletenessTests`, a `ReadOnlySyncRepositoryGuard` write-throw for every new write method, and the `JIM.InMemoryData` double, exactly as the initial-password store did.

Requirement 13 divergence from initial passwords, encoded in the parking rules: a policy-rejected *synchronised* password parks and is never regenerated; the only remedies are manual retry (after the administrator fixes the target policy) or a newer change coalescing over it.

#### Encryption: dedicated purpose

`CredentialProtectionService` gains a second protector under `JIM.PasswordSync.v1` with a distinct value prefix (`$JIMPW$v1$`), exposed as `ProtectPassword`/`UnprotectPassword` beside the existing members. Static initial passwords stay under `JIM.Credentials.v1` (divergence 4); nothing re-encrypts. The narrow `ICredentialProtection` interface Connectors receive is unchanged: decryption happens in the delivery processor, and the cleartext exists only in process memory for the duration of the attempt.

#### Configuration: `ConnectedSystemPasswordSynchronisation`

A 1:1 owned entity on `ConnectedSystem` rather than flat columns: "Configured" = the row exists (requirement 1's state without a redundant flag), and Phase 2 will add inbound settings to the same row.

- `Enabled` (`bool`): independent of configured; disabled accumulates, never discards (requirement 2)
- `TargetObjectTypeId` (`int`): which Connected System Object Type receives passwords; picker over `ObjectTypes.Where(ot => ot.Selected)` per the `SyncRuleDetailsTab` precedent
- `MaxRetries` (`int`), `RetryBackoffBase` (`TimeSpan`): requirement 5's delivery options; backoff is `base × 2^(AttemptCount-1)` capped at the TTL
- `RequireSecureTransport` (`bool`): closes divergence 1 the way the PRD prescribes; when set, `LdapConnectorPassword.OpenPasswordConnection` refuses an unencrypted connection instead of warning
- TTL: reuses `ConnectedSystem.InitialPasswordTimeToLive` / `EffectiveInitialPasswordTimeToLive` (divergence 5)

Configuration is only offered where the Connector Definition carries the password capability flag; hidden otherwise (requirement 4). A comparison guard in the `SyncRuleInitialPasswordComparisonCompletenessTests` style stops a future setting being added without deciding its enable-drain/release semantics.

#### Fan-out and Activities

`PasswordSynchronisationServer` (`src/JIM.Application/Servers/`), exposed as `JimApplication.PasswordSynchronisation`, constructed with the same lazy `Func<ICredentialProtectionService>` idiom as `InitialPasswordDeliveryServer`.

`QueuePasswordChangeAsync(metaverseObject, password, options, initiator)`:
1. Resolve targets: enabled, configured Connected Systems where the identity has a Connected System Object of the target Object Type, or none yet (queued per Resolved Decision 2).
2. Create the parent Activity: new `ActivityTargetType.PasswordSynchronisation` (25), existing `ActivityTargetOperationType.SetPassword` (divergence 2), `MetaverseObjectId` set, `TargetName` = identity display name.
3. Zero targets: complete the parent with an explicit no-op outcome message (requirement 14) and stop.
4. Otherwise one batched UPSERT for all targets (single round trip at fan-out scale, per the non-functional requirement), then enqueue delivery.

Child Activities are created per delivery attempt outcome by the processor (`ParentActivityId` = the row's `ActivityId`, `TargetContext` = Connected System name so the existing per-system filter works for free), following `SetPasswordOnAccountsAsync`'s fan-out Activity shape. New `ActivityTargetCategory.PasswordSynchronisation` (5) maps the new target type; `ActivityTargetTypeCategories.Map`, `ActivityList.GetCategoryLabel`, and `ActivityTargetCategoryTests` all change together (the exhaustiveness test enforces it).

**Entry points routed through the queue:** the Metaverse-Object-level set (portal dialog on the identity page, the new `POST /api/v1/metaverse/objects/{id}/password` endpoint, and `Set-JIMMetaverseObjectPassword`) becomes a queued fan-out when at least one system has Password Synchronisation enabled; against zero enabled systems the existing synchronous multi-account path remains so the feature changes nothing until it is switched on. The per-Connected-System-Object set stays synchronous: it is a targeted repair tool, not a propagation.

#### Delivery: `PasswordDeliveryWorkerTask`

A new `WorkerTask` subclass (nullable `ConnectedSystemId`; null = sweep every system with due work), following `TemporalScopeReconciliationWorkerTask`'s no-configuration shape and dispatched to a static `PasswordDeliveryTaskProcessor` per the `ConfigurationChangePreviewTaskProcessor` pattern. Registered in the four places a task type touches: the model, the `DbSet` + migration, `TaskingRepository.CreateWorkerTaskAsync`, `TaskingServer.CreateWorkerTaskAsync`.

Enqueued on: a password change (fan-out), enabling a system (drain-on-enable, requirement 3), manual retry, and by worker housekeeping when `NextRetryAt` has fallen due and no delivery task is already queued or running (the existing 60-second housekeeping cadence bounds retry latency; deduplication via the same queued-task check other task types use).

The processor per Connected System: expire overdue rows first (recorded, not swept), fetch due `Pending` rows oldest-first under a per-pass bound (the `MaximumAccountsPerPass` pattern), open the password connection once, decrypt-deliver-classify per row, persist outcomes in a `finally`, close the connection. Classification reuses `PasswordSetFailureReason`: transient and configuration faults schedule the next retry with exponential backoff until `MaxRetries`, then park with the reason preserved; policy rejection and unsupported operation park immediately; success deletes the row and completes the child Activity. Exhausted retries are `Parked` with the terminal reason, visible and manually retryable (requirement 10). A row whose `ConnectedSystemObjectId` is still null re-resolves the account at attempt time, so the provisioning race self-heals inside the TTL.

The queue survives restarts with no loss or duplication: rows are only deleted after a confirmed success, the unique index prevents duplicates, and re-delivering an already-set password (crash between set and delete) sets the same value, which is idempotent from the user's point of view.

#### Retention: Schedule-driven trim

Requirements 28 to 30 plus divergence 6: a new `ScheduleStepType.HistoryRetentionCleanup` executing the existing `ChangeHistoryServer.DeleteExpiredChangeHistoryAsync` (extended with the password queue's terminal rows and a new `History.PasswordEventRetentionPeriod` Service Setting for password Activities), seeded as a built-in daily Schedule via `SeedBuiltInSchedulesAsync` and restored by factory reset, with the worker-housekeeping history-cleanup timer removed in the same change. This deliberately delivers #1118 for all retention types at once rather than running two trim mechanisms side by side; it is the largest scope decision in this plan and is isolated in its own phase so it can be deferred without blocking the rest.

### Connector coverage

The queue, fan-out, and delivery speak only `IConnectorPasswordManagement`, and requirement 4 hides Password Synchronisation configuration on any Connected System whose connector does not declare `SupportsPasswordSet`. Nothing in this plan is LDAP-specific; a connector that later implements the capability joins the feature with no queue changes.

- **LDAP Connector: in scope now.** The only production implementer today; this plan's one connector change is `RequireSecureTransport` hardening.
- **SCIM 2.0 Client Connector: natural follow-on, not this plan.** SCIM's `password` is a standard write-only attribute, so an outbound set is a PATCH away; the connector currently declares the capability false precisely so JIM does not call into a gap. Implementing `IConnectorPasswordManagement` there is a small self-contained feature (no policy discovery exists over SCIM, which does not matter for synchronised passwords because JIM never generates them). Raise as its own issue once this plan's delivery path exists to consume it.
- **SQL Connector: needs its own design decision first.** Writing a password into a table column persists cleartext (or forces JIM to guess the application's hash format), which is the same hazard the PRD's rejected file channel names. The defensible shape is delegating to a customer-supplied stored procedure so the target system owns its own hashing, and that is a design conversation for when the connector completes, not an assumption to bake in here.
- **File Connector: never, by design.** A password written to a file is cleartext at rest with no audit and no revocation; the PRD's "Rejected and deferred inbound channels" reasoning applies equally outbound. The capability stays false and the configuration stays hidden.
- **Mock connector: already implements the capability** and is how Phases 2 and 3 are unit tested.

## Implementation Phases

Each phase is TDD, red first, and lands with its tests, docs, and changelog entries; write parity ships with each surface in its own phase per the surface-parity rule.

### Phase 1: Configuration and encryption foundation

- `ConnectedSystemPasswordSynchronisation` entity, EF configuration, migration; comparison/completeness guard tests
- Dedicated protection purpose in `CredentialProtectionService` (`ProtectPassword`/`UnprotectPassword`, prefix, round-trip and isolation tests: a value protected under one purpose must not unprotect under the other)
- Configuration parity: portal tab on `ConnectedSystemDetail` (visible only when the connector has the capability), REST create/read/update/enable/disable on `SynchronisationController`, `Get-/Set-JIMConnectedSystemPasswordSynchronisation` cmdlets with Pester tests (requirements 1, 2, 4, 5, 32)
- Wire `CredentialAttributes.HasCredentialLikeName` into Attribute Flow configuration validation as a warning (closes requirement 16's missing call site)

### Phase 2: Queue, fan-out, and Activities

- `PendingPasswordChange` + status enum, EF configuration, unique coalescing index, migration, bulk-columns constants, repository methods on `ISyncRepository` (UPSERT, get-due, record-attempts, delete, expire, release, counts), guard and in-memory implementations
- `PasswordSynchronisationServer`: `QueuePasswordChangeAsync` with coalescing, zero-target no-op Activity, batched writes
- New Activity target type and category, label, map, and exhaustiveness tests (requirements 6, 7, 8, 14, 23, 24)
- Unit tests: coalescing supersedes, fan-out scoping (enabled + configured + object type), unprovisioned target queues, no-op recorded, payload encrypted at rest, never-log invariant

### Phase 3: Delivery, retry, and drain

- `PasswordDeliveryWorkerTask` + static processor; the four task-type registration points; housekeeping due-retry enqueue
- Expiry-first pass, backoff schedule, park rules (policy rejection never regenerates, requirement 13), max-retries exhaustion, child Activity per outcome, delete-on-success
- Drain-on-enable and configuration-change release semantics (requirement 3); manual retry resets `NextRetryAt` and re-enqueues
- Unit tests against `MockCallConnector` (which already implements `IConnectorPasswordManagement`); `RequiresPostgres` round-trip tests for the UPSERT and the due-work query

### Phase 4: Surfaces and reporting

- Queue page (`/admin/password-synchronisation`): queued/parked/expired with target, status, reason, attempt count, next retry; retry one, retry filtered selection, cancel/delete; DTO never carries the payload (requirements 21, 22)
- REST queue read/retry/cancel endpoints and PowerShell cmdlets (requirement 33); new `POST /api/v1/metaverse/objects/{id}/password` closing the existing MVO-endpoint gap, with the secure-transport check (`Request.IsHttps` reject, requirement 34) applied to every password-accepting endpoint
- Connected System list: state on `ConnectedSystemHeader` (all three projection sites), indicator chip, `passwordsync` sort arm, filter control (requirement 26)
- Metaverse Object detail: admin-only Password Synchronisation panel via the `AuthorizeView` tab precedent, listing that identity's password Activities with per-system outcomes (requirement 25)
- `LdapConnectorPassword` honours `RequireSecureTransport` as a refusal (divergence 1 closed as designed)

### Phase 5: Schedule-driven retention

- `History.PasswordEventRetentionPeriod` Service Setting (seeded, typed accessor, classifier entry, History API + DTO)
- `ScheduleStepType.HistoryRetentionCleanup`, built-in daily Schedule (seeded idempotently, factory-reset restored), step dispatch in `SchedulerServer.QueueStepAsync`, worker execution path; remove the housekeeping history-cleanup timer; absorb the initial-password trim (divergence 6) and add the password queue trim, batched with summary statistics (requirements 28, 29, 30; delivers #1118)

### Phase 6: Integration, documentation, and security review

- New integration scenario: configure and enable Password Synchronisation on the Samba AD system, change a password via the API, assert delivery by binding; disabled-accumulate-then-drain and coalescing assertions (Scenario 3's shape); registered in `Run-IntegrationTests.ps1`
- Public docs: Password Synchronisation concept and how-to, LDAP connector reference update, Activities category reference, REST and PowerShell reference; `DEVELOPER_GUIDE.md` password channel section; component diagrams
- Security review pass against the never-log/never-serialise invariants (including the `Invoke-JIMApi` debug-stream body logging, which must redact password bodies); changelog; PRD Implementation Progress refresh

## Success Criteria

The PRD's acceptance criteria, all of which this plan covers except the Phase 2 (inbound) items. Concretely measurable: three password changes for one identity against a disabled system leave one queue row; enabling delivers it without intervention; a policy rejection parks with the target's reason and never generates a substitute; an expired row is visible as `Expired`, never silently gone; a worker restart mid-queue loses and duplicates nothing; no password value appears in any log, Activity, DTO, or API response under test.

## Risks and Mitigations

- **Coalescing race between two near-simultaneous changes.** Mitigated in the database: the unique index plus `ON CONFLICT DO UPDATE` makes last-write-wins atomic; no application-side read-modify-write.
- **Double delivery on crash between set and delete.** Accepted and safe: re-setting the same password is idempotent for the user; the child Activity records both attempts honestly.
- **`RequireSecureTransport` refusing valid signed-and-sealed binds.** It is per-system opt-in (default off), exactly why divergence 1 chose an option over a hard-coded rule; the warning-only behaviour remains the default.
- **Phase 5 effectively implements #1118, widening scope.** Isolated in its own phase; deferring it leaves the password trim in housekeeping beside the initial-password one (two entries in one mechanism, not two mechanisms), which is acceptable temporarily but must not ship as the final state.
- **Key-ring loss now strands queued passwords, not just connector credentials.** Documented risk the PRD accepts (#952); the queue's TTL bounds the damage; the docs phase reiterates the backup requirement.
- **Backoff arithmetic overflow on high attempt counts.** Cap the computed delay at the TTL; a delay past `ExpiresAt` expires instead.

## Dependencies

- No new NuGet packages.
- #1118 is delivered by Phase 5 rather than depended on; if it lands independently first, Phase 5 collapses to adding two trim calls to its step.
- #952 (key-ring backup docs) becomes more important, not blocking.

## Out of Scope

Phase 2 inbound capture (ingress API, inbound password mapping on import), the Domain Controller agent, SSPR, and defensive password filtering (#1120), which only requires that `PendingPasswordChangeStatus` and the Activity outcomes leave room for a future "rejected by policy" terminal outcome, and the park model already provides that.
