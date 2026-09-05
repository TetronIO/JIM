# Password Synchronisation

- **Status:** Doing (Phase 1 delivered in full: every acceptance criterion below is met and evidenced; Phase 2 inbound capture is untouched)
- **Created:** 2026-07-25
- **Author:** Tetron
- **Issue:** [#1119](https://github.com/TetronIO/JIM/issues/1119)
- **Note:** `Doing` refers to Phase 2 only. Phase 1 (this document's own machinery: per-system configuration, the encrypted queue, fan-out, coalescing, retry and backoff, the queue page, retention, and the three surfaces) is delivered and evidenced against every [Acceptance Criterion](#acceptance-criteria) below; see [Implementation Progress](#implementation-progress) for the per-requirement record. The foundation it was built on landed under [#1121](https://github.com/TetronIO/JIM/issues/1121), [#1172](https://github.com/TetronIO/JIM/issues/1172), [#1221](https://github.com/TetronIO/JIM/issues/1221) and [#1273](https://github.com/TetronIO/JIM/issues/1273). What remains is Phase 2 inbound capture, now the ingress API alone: inbound password mapping on import was carved out to [#1563](https://github.com/TetronIO/JIM/issues/1563) for future consideration, because nothing has asked for it and it is the only part of this feature that is not event-shaped.

## Problem Statement

JIM synchronises identity attributes but has no way to synchronise passwords. An administrator who provisions a new user into Active Directory through JIM cannot set that user's initial password, and an organisation whose users change their password in one system has no way to propagate that change to the others. Every JIM customer today must solve password consistency with a second product or with scripts.

This is a recognised post-MVP gap (`engineering/plans/done/MVP_DEFINITION.md`), and it is a blocker for operators migrating from traditional ILM systems, where per-connector password synchronisation and a Domain Controller capture agent are long-standing, expected capabilities.

Password data raises requirements that JIM's existing synchronisation machinery deliberately does not meet. Attribute values are persisted, previewed, versioned in change history, and reconciled by re-import. A password must be none of those things: it is write-only, must never be readable back, must never appear in a preview or a change record, and cannot be confirmed by import because no directory will disclose it. Password Synchronisation therefore needs its own channel rather than an Attribute Flow.

## Goals

- An administrator can configure Password Synchronisation on a Connected System, and enable or disable it independently of that configuration; we can confirm this works by toggling the setting and observing queued events drain on enable.
- A password change for a Metaverse Object is delivered to every enabled, Password-Synchronisation-configured Connected System, with success and failure recorded per target system.
- A failed delivery is retained in a durable queue with the error visible to an administrator, is retried automatically with backoff, and can be retried manually from the portal.
- Password events are recorded as Activities and are filterable in the Activities list without adding bespoke filter controls.
- An administrator can see the password event history for a single identity from the Metaverse Object detail page.
- The Connected System list shows which systems have Password Synchronisation enabled, and can be filtered and sorted on it.
- Queue and Activity growth are bounded automatically by a Schedule-driven trim, consistent with existing history retention.
- No cleartext password is ever written to a log, an Activity, a change record, an export preview, or any persisted field other than the encrypted queue payload; we can confirm this by inspection and by a security review before merge.
- Parity across all three surfaces: portal, REST API, and PowerShell.

## Non-Goals

- **Passwords as attributes.** Password values will never be modelled as a Metaverse Attribute or a Connected System Object Type attribute, never imported, and never stored on a Metaverse Object or Connected System Object.
- **Password hash synchronisation.** JIM synchronises password *changes* captured in cleartext at the moment of change, not hashes. Directory hashes are not extractable through supported interfaces and are not portable between directory types.
- **Password history or complexity policy management.** JIM delivers a password to a target system; that system enforces its own policy and history. JIM will report a policy rejection as a failure, not attempt to pre-validate against a remote policy.
- **Self-service password reset (SSPR).** An end-user-facing reset portal is a separate feature with its own authentication and verification requirements. This PRD covers administrator-initiated and externally-captured password changes only.
- **The Domain Controller capture agent itself.** Phase 2 delivers the inbound ingress API that such an agent would call. The native Windows agent is a separate deliverable with its own toolchain, signing, and release cycle; see "Phase 2 prerequisites" below.
- **File-based (CSV) password ingest.** Deliberately deferred; see "Rejected and deferred inbound channels". Note that this leaves a source system supplying joiners' initial passwords with no committed route into the password channel: the alternative this once pointed at, inbound password mapping on import, is parked at [#1563](https://github.com/TetronIO/JIM/issues/1563). A repeat request for either is the demand signal to pick that up rather than to reopen the file question.
- **Adopting a third-party password filter as a dependency.** Existing open-source filters are design references, not components we ship; see "Prior art".

## User Stories

1. As an identity administrator, I want to set a user's initial password when JIM provisions their account, so that the account is usable without a manual follow-up step.
2. As an identity administrator, I want a password change to reach every system that user has an account in, so that they have one password rather than several.
3. As an identity administrator, I want to see why a password failed to reach a system and retry it once I have fixed the cause, so that I can resolve integration problems without waiting for the next scheduled run or losing the event.
4. As an identity administrator, I want to configure Password Synchronisation for a system before I switch it on, so that I can stage the configuration and enable it during a change window.
5. As a security officer, I want a complete, immutable audit trail of who changed whose password, when, and whether each target system accepted it, without that trail containing the password.
6. As an operator migrating from a traditional ILM system, I want Password Synchronisation configured per Connected System with an explicit enable toggle, so that the model matches the one I already operate.

## Requirements

### Functional Requirements

**Configuration**

1. Password Synchronisation is configured on the Connected System, not on a Synchronisation Rule. A Connected System has a Password Synchronisation configuration comprising, at minimum: a `Configured` state, an `Enabled` toggle, the target Connected System Object Type, and connector-specific options.
2. The `Enabled` toggle is independent of `Configured`. A Connected System that is configured but disabled still accumulates queued password events; it does not discard them.
3. When a disabled Connected System is enabled, its queued events are processed automatically without administrator intervention.
4. Password Synchronisation can only be configured on a Connected System whose connector declares the password capability. The option is hidden, not merely disabled, on connectors that do not support it.
5. Each configuration exposes per-system delivery options: maximum retry count, retry backoff, event time-to-live, and whether a secure transport is mandatory for password delivery.

**Queue and delivery**

6. A password change for a Metaverse Object fans out at the Metaverse to one queued Pending Password Change per in-scope Connected System. There is never a direct system-to-system password flow.
7. The queued payload is encrypted at rest using the existing credential protection service under a dedicated purpose, distinct from connector settings.
8. **Queued events coalesce per (Metaverse Object, Connected System): a newer password change supersedes any pending older one for the same target.** The queue holds the latest intended password per target, never a replayable sequence of historical passwords.
9. Every queued event carries an expiry. An event that exceeds its time-to-live is expired rather than delivered, and the expiry is recorded as a distinct outcome, not a silent drop.
10. Delivery failures increment an error count, record the error type, message, and timestamp, and schedule a retry with exponential backoff up to the configured maximum. Exhausting retries moves the event to a terminal failed state that remains visible and manually retryable.
11. A successfully delivered event is deleted from the queue immediately. The permanent record of the event is its Activity, not its queue row.
12. Failures are classified so that an administrator can distinguish a transient condition (system unavailable, timeout) from a configuration fault (bad credentials, insufficient rights, insecure transport refused) from a target rejection (password policy violation).
13. **A synchronised password rejected by the target's password policy is never replaced with a generated substitute.** The value originates from the user, so setting a different password on the target would defeat the purpose of synchronising it. A policy rejection fails visibly, with the target's reason recorded, for the administrator to resolve at the source. This is the deliberate point of divergence from initial passwords ([#1121](https://github.com/TetronIO/JIM/issues/1121)), where the value is generated by JIM and therefore can be regenerated under corrected settings.
14. A password change that targets zero enabled systems is still recorded as an Activity, with an outcome that makes the no-op explicit.

**Preventing cleartext passwords leaking into the Metaverse**

15. Well-known credential attributes are denylisted in connector schema handling: they cannot be selected for import, cannot be targeted by an Attribute Flow, and are reachable only through the password channel. The initial list for LDAP is `unicodePwd`, `userPassword`, `dBCSPwd`, `ntPwdHistory`, `lmPwdHistory`, `supplementalCredentials`, `unixUserPassword`, and `msDS-ManagedPassword`. This is distinct from, and additional to, the existing protected-attribute defaults list, which concerns clearing attributes rather than credentials.
16. Where an Attribute Flow targets an attribute whose name matches a credential-like pattern outside that list, configuration validation raises a warning explaining that the password channel exists and that flowing a password as an attribute persists it in Metaverse Object attribute values, change history, Pending Exports, export previews, search results, and database backups.

**Connector capability**

17. A new capability interface, provisionally `IConnectorPasswordManagement`, exposes a set-password operation against a Connected System Object, plus open and close semantics consistent with the existing export capability interfaces.
18. A corresponding capability flag is added to the connector capability contract and mirrored on the persisted Connector Definition.
19. The LDAP connector implements the capability, and **chooses how to set the password from what it has already discovered about the directory**, not from a setting an administrator has to get right. The connector identifies the directory server product from the RootDSE for its other operations; that same identification, plus whether the RootDSE advertises the RFC 3062 Password Modify extended operation, selects the mechanism. Active Directory writes `unicodePwd` using the required quoted UTF-16LE encoding, and **must refuse to transmit unless the connection is LDAPS**. Any other directory uses the Password Modify extended operation, so the directory applies its own configured hashing to the value. JIM never writes `userPassword` directly: doing so would store whatever the client sent, bypassing the directory's own hashing scheme, which is also why the attribute is on the credential denylist in requirement 15. A directory that is neither Active Directory nor advertises the extended operation is reported as a configuration fault naming what is missing, rather than falling back to an unsafe write.
20. The connector supports an initial-password-on-create flow and an optional "user must change password at next sign-in" behaviour where the target system has such a concept.

**Reporting and audit**

21. A Password Synchronisation queue page lists queued, failed, and expired events with target Connected System, target object, status, error detail, attempt count, and next retry time. It never displays, and its backing DTO never carries, the password value.
22. The queue page supports manual retry of a single event and of a filtered selection, and supports cancelling or deleting an event.
23. Every password change event produces an Activity: a parent Activity for the change and a child outcome per target Connected System recording success or failure.
24. A new Activity target type is added for Password Synchronisation, mapped to a **new Activity target category** so the existing Activities list quick-filter isolates password events with no new filter controls. Per-Connected-System filtering comes for free from the existing filter options provided the Activity sets its target context to the Connected System name.
25. The Metaverse Object detail page gains a Password Synchronisation panel showing that identity's recent password events and their per-system outcomes. The panel is visible to administrators only.
26. The Connected System list view indicates Password Synchronisation state per row, and supports filtering and sorting on it.
27. Queue rows and their Activities are reconciled: a queue row links to the Activity that created it, and the Activity records the terminal outcome. The queue holds operational state only; the Activity is the durable audit record and outlives the queue row.

**Retention**

28. The Pending Password Change queue is trimmed automatically by a Schedule, not by worker housekeeping, consistent with the direction of #1118. Terminal-state rows older than a retention period are removed; live rows are never trimmed.
29. Password event Activities are retained under their own retention class and Service Setting, alongside the existing general, configuration-change, and security-event retention periods.
30. Trim operations are batched using the existing cleanup batch-size setting and report summary statistics.

**Surfaces**

31. Setting a password is available on all three surfaces: a portal dialog, a REST endpoint, and a PowerShell cmdlet.
32. Password Synchronisation configuration (create, read, update, enable, disable) is available on all three surfaces.
33. Queue read and manual retry are available on all three surfaces.
34. REST endpoints that accept a password are administrator-only, are excluded from request logging, and reject the request unless the transport is secure.

### Non-Functional Requirements

- A password value exists in cleartext only in process memory and only for the duration of a delivery attempt. At rest it is always encrypted under the dedicated protection purpose.
- No code path may log, sanitise-and-log, include in an exception message, include in an OpenAPI example, or serialise a password into any DTO returned to a client.
- Fan-out and queue writes must not materially slow the enclosing operation at customer scale; a password change for one identity across ten Connected Systems is a single batched write.
- Queue processing must tolerate a Connected System being unavailable for an extended period without unbounded growth, via coalescing, time-to-live, and scheduled trim together.
- The feature must work air-gapped, with no cloud key service.
- The queue must survive a worker restart with no lost or duplicated deliveries.

## Examples and Scenarios

### Scenario 1: Provisioning sets an initial password ✅ Delivered

**Given**: A Connected System "Corporate AD" has Password Synchronisation configured and enabled, and a Synchronisation Rule provisions new joiners into it.
**When**: A new identity is provisioned and an initial password is generated.
**Then**: The account is created, the password is set on the new account over LDAPS, "user must change password at next sign-in" is applied, the queue row is deleted on success, and an Activity records a successful password set against "Corporate AD" with no password value anywhere in the record.

Delivered under [#1121](https://github.com/TetronIO/JIM/issues/1121), and the reason this PRD is `Doing` rather than `Planned`. Two differences from the wording above, neither of which changes the outcome an administrator sees:

- **The initial password is configured on the Synchronisation Rule, not by enabling Password Synchronisation on the Connected System**, because that configuration does not exist yet. The rule is where the setting belongs anyway (see #1121's reasoning); when this feature's per-system configuration lands, it governs *synchronised* passwords rather than taking this one over.
- **The row that is deleted on success is a `PendingInitialPassword`, not this feature's queue row.** It records the intent only, never a value, and its lifecycle is described under Implementation Progress below.

### Scenario 2: A target system is unavailable, then recovers

**Given**: "Corporate AD" is enabled for Password Synchronisation but its service account password has expired.
**When**: An administrator changes a user's password.
**Then**: Delivery fails, the queue row records an authentication failure with the error message and attempt count, retries occur with backoff, and the Activity shows the failure. After the administrator corrects the service account credential and clicks Retry on the queue page, the delivery succeeds, the row is removed, and a new successful outcome is recorded.

### Scenario 3: Disabled system accumulates and then drains

**Given**: "HR Portal" has Password Synchronisation configured but disabled during a maintenance window.
**When**: Three password changes occur for user A and one for user B, and the administrator then enables Password Synchronisation for "HR Portal".
**Then**: The queue holds exactly two rows before enabling (one per identity, the latest password for user A having superseded the earlier two), and on enable both are delivered automatically without administrator intervention.

### Scenario 4: Stale event expires rather than resurrecting an old password

**Given**: "Legacy Directory" has been disabled for longer than the configured event time-to-live, and holds a queued password change for user C.
**When**: The Schedule runs, or the system is re-enabled.
**Then**: The event is marked Expired rather than delivered, and the Activity records the expiry with its reason, so an administrator can see that user C's password was never propagated to that system rather than silently assuming it was.

### Scenario 5: Auditing a single identity

**Given**: A security officer is investigating an account.
**When**: They open the Metaverse Object detail page as an administrator.
**Then**: A Password Synchronisation panel shows the recent password events for that identity, each with initiator, timestamp, and per-Connected-System outcome, and no password values.

## Constraints

- Must work in air-gapped, on-premises deployments with no cloud key management.
- Must not name competing identity products in code, comments, or documentation.
- Must reuse the existing credential protection service rather than introducing a second encryption mechanism, and must therefore inherit its key-ring backup requirements (see #952).
- Must not introduce a new NuGet package without prior approval.
- Encryption keys are shared between the web and worker services through the existing Data Protection key ring; the feature must not assume single-process operation.
- Existing deployments must upgrade cleanly: Password Synchronisation is unconfigured everywhere by default and changes no existing behaviour.

## Affected Areas

| Area | Impact |
|------|--------|
| Database | New Pending Password Change table with indexes for queue polling and coalescing; new Connected System Password Synchronisation configuration; new Service Settings for retention; migration |
| Models | New capability interface and flag; new queue entity and enums; new Activity target type and category; new retention constants |
| Connectors | New password capability implemented by the LDAP connector (Active Directory `unicodePwd`, RFC 3062 Password Modify elsewhere, mechanism chosen from the discovered directory type, LDAPS enforcement); credential-attribute denylist in schema handling; Connector Factory wiring; Mock connector support for testing |
| Application | New server for password change orchestration, fan-out, queue management, and retry; credential protection under a new purpose; Activity recording; retention trim |
| Worker | Queue processing and retry execution; Schedule-driven trim step |
| Scheduler | New built-in Schedule for queue and password-event trim, seeded idempotently and restored by factory reset |
| API | Set-password endpoint; Password Synchronisation configuration endpoints; queue read and retry endpoints; Phase 2 inbound ingress endpoint |
| PowerShell | Set-password cmdlet; configuration cmdlets; queue read and retry cmdlets; Pester tests |
| UI | Connected System Password Synchronisation configuration; Connected System list indicator, filter, and sort; Password Synchronisation queue page; Metaverse Object detail panel; Activities list category chip |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/` (new page) | Password Synchronisation concept and how-to: configuring a Connected System, the queue, retry, retention, and the security model |
| `docs/` (LDAP connector reference) | Password capability, AD versus generic mode, LDAPS requirement |
| `docs/` (Activities reference) | New Password Synchronisation category and what its Activities record |
| `docs/` (REST and PowerShell reference) | New endpoints and cmdlets |
| `engineering/DEVELOPER_GUIDE.md` | New password channel component and its relationship to the export pipeline |
| `engineering/COMPLIANCE_MAPPING.md` | Handling of cleartext secrets in transit and at rest |
| `docs/assets/diagrams/` | Connector and Worker component views gain the password channel |

## Dependencies

- Encryption key-ring backup documentation (#952) becomes materially more important; a lost key ring now means undeliverable queued passwords as well as unreadable connector credentials.
- Write-only handling of encrypted secret fields (#951) shares the "never round-trip the ciphertext to the browser" requirement and should be resolved consistently.
- Schedule-based trim aligns with moving Activity cleanup to a Schedule (#1118); if that lands first, this feature should follow its pattern rather than inventing a second one.
- Initial password generation and delivery on provisioning (#1121) shares this feature's connector set-password foundation and write-only invariant, and is intended to land first as that foundation's initial consumer. Build the connector set-password capability once and share it; do not implement it twice.

## Resolved Decisions

These were open during drafting and have since been decided; they are settled inputs to the implementation plan, not still-open questions.

1. **Fan-out scope: all enabled systems, no scoping filter.** A password change fans out to every Connected System that has Password Synchronisation enabled and in which the identity has a Connected System Object. There is deliberately no per-system scoping expression in v1; keep it simple. (Scoping can be added later if a real need appears.)
2. **Unprovisioned target: queue it.** A password change for an identity that has no Connected System Object in an enabled target yet is queued rather than failed, bounded by the event time-to-live, so the provisioning-then-password race resolves itself when the account appears.
3. **Initial-password generation: expression engine for v1, with a first-class generator tracked separately and built first.** v1 reuses the existing expression engine as the interim answer, but the first-class generator has since been promoted to its own feature, [#1121](https://github.com/TetronIO/JIM/issues/1121) (initial password generation and delivery on provisioning), which is a real provisioning gap in its own right and is intended to land ahead of the sync machinery. #1121 shares this feature's connector set-password foundation; the recommended sequencing is to build that connector capability once, land #1121 as its first consumer, then layer this PRD's queue, fan-out, and inbound capture on top. Either way the v1 model must keep initial-password sourcing behind a seam so #1121's generator can plug in without reworking the queue or delivery path.
4. **Default event time-to-live: 7 days, configurable per Connected System.** Long enough to ride out a realistic outage, short enough not to resurrect a stale password indefinitely.

## Implementation Progress

This PRD's Resolved Decisions committed to building the connector set-password foundation **once**, under [#1121](https://github.com/TetronIO/JIM/issues/1121), and layering this feature's queue, fan-out, and inbound capture on top. That foundation has now shipped, along with three further consumers of it: [#1172](https://github.com/TetronIO/JIM/issues/1172) (one password across several of a person's accounts), [#1221](https://github.com/TetronIO/JIM/issues/1221) (the outstanding-work lifecycle behind initial passwords) and [#1273](https://github.com/TetronIO/JIM/issues/1273) (a static initial password). Several of the functional requirements below are therefore already met, and #1221 in particular built a durable per-account work store whose shape this feature's queue should follow rather than reinvent. This section records all of that, so that the eventual Password Synchronisation implementation extends what exists rather than duplicating it. It is a status record, not a change of scope: no requirement here has been added, removed, or reworded.

Last reviewed 2026-08-28, against `main` with the whole of Phase 1 merged.

### Requirements already satisfied

| Req | State | Where it landed |
|-----|-------|-----------------|
| 15 (credential attribute denylist) | Done | `CredentialAttributes` (`JIM.Models/Staging/`). The eight named LDAP attributes are blocked from import, from schema selection, and from being the source or target of an Attribute Flow, Object Matching Rule, or scoping criterion. A schema refresh reports what it blocked via `SchemaRefreshResult.BlockedCredentialAttributes`. |
| 17 (`IConnectorPasswordManagement`) | Done | `JIM.Models/Interfaces/IConnectorPasswordManagement.cs`, with open and close semantics mirroring the export capability interfaces. |
| 18 (capability flag, mirrored on Connector Definition) | Done | `IConnectorCapabilities`, mirrored to `ConnectorDefinition`. |
| 19 (LDAP sets passwords, mechanism chosen from the discovered directory) | Done, with one divergence | `LdapConnectorPassword`: Active Directory writes `unicodePwd` as a quoted UTF-16LE value; every other directory uses the RFC 3062 Password Modify extended operation, and a directory advertising neither is failed as a configuration fault rather than falling back to a `userPassword` write. The mechanism comes from the RootDSE the connector already reads, with no administrator setting. The mandatory-LDAPS clause was deliberately not implemented as written; see Divergences. |
| 20 (initial password on create, change-at-next-sign-in) | Done | `InitialPasswordDeliveryService` and `InitialPasswordDeliveryServer`, driven from export execution; `PasswordExpiryBehaviour` carries the change-at-next-sign-in behaviour. The delivery is no longer best-effort: #1221 gave an account owed a password a durable `PendingInitialPassword` row with its own lifecycle, and #1273 added a third source of the value (a static password the administrator sets) alongside the discovered and custom generator settings. |
| 31 (set a password on all three surfaces) | Done | Portal set-password dialog on both the Connected System Object and the Metaverse Object, `POST .../connector-space/{csoId}/password`, and the `Set-JIMConnectedSystemObjectPassword` / `Set-JIMMetaverseObjectPassword` cmdlets. |

### Requirements partially satisfied

| Req | Outstanding |
|-----|-------------|
| 16 (warn on a credential-like attribute outside the denylist) | The heuristic shipped (`CredentialAttributes.HasCredentialLikeName`, deliberately broad and warn-only) but has **no call site**: no configuration validation raises the warning yet. Wiring it into Attribute Flow configuration is outstanding. |
| 34 (password endpoints are administrator-only, unlogged, secure-transport-only) | Authorisation and the never-log invariant are met: the endpoint is `[Authorize(Roles = "Administrator")]`, logs the target object and never the value or its length, and the connector redacts the password out of any message the directory echoes back. There is no per-endpoint secure-transport check; the deployment's HTTPS enforcement is currently the only guard. |

Requirement 12's failure classification also exists in part, as `PasswordSetFailureReason` (transient, configuration fault, policy rejection, target not found), built for the synchronous set-password path. The queue will consume the same enum rather than defining a second one.

Work delivered under #1121 that this PRD assumed but did not require: Connected System password policy discovery (`IConnectorPasswordPolicyDiscovery`), a first-class password generator (`PasswordGeneratorService`, superseding Resolved Decision 3's interim expression-engine answer), Tier 1 preflight checks, and a control-access-right evaluation for the service account. Requirement 5's per-system delivery options should be added alongside the discovered-policy configuration already on the Connected System, not beside it.

### The initial-password work store, and what this feature should take from it

#1221 built `PendingInitialPassword` (`JIM.Models/Transactional/`): a durable row per account owed an initial password, written when a Create export succeeds and removed when the password is set. It is not this feature's queue, and it must not be mistaken for it: it is keyed on a Connected System Object rather than on a (Metaverse Object, Connected System) pair, it carries no password value at all (the password is generated at the moment of delivery), and so it has nothing to coalesce and nothing to encrypt. What it does have is the operational lifecycle requirements 9 to 12 and 21 to 22 describe, already argued through and already tested, and the queue should adopt its shape rather than settle these questions a second time:

- **Delete on success, and keep no delivered state.** The record is a list of work outstanding; the Activity is the history. This is requirement 11 already decided, and `PendingInitialPasswordStatus` documents the reasoning.
- **Two live states, not one.** `Pending` means time or the environment may resolve it and JIM will try again; `Parked` means only a person can, so JIM has stopped trying. A policy rejection parks, because the same settings produce another password the target refuses for the same reason. Requirement 12's `PasswordSetFailureReason` is what classifies into them, and requirement 13's "never substitute a generated password for a synchronised one" is the point where this feature must diverge: a parked *synchronised* password cannot be released by correcting generator settings, because there are none.
- **A configuration change releases what it parked.** `ReleaseParkedForSyncRuleAsync` sets a rule's parked accounts retrying when its settings are saved, so correcting the fault is the whole remedy. Requirement 3's drain-on-enable is the same mechanic against a different trigger.
- **Expiry is recorded, not swept.** An `Expired` row is retained and reported, which is requirement 9's "explicit outcome, not a silent drop".
- **The portal surfaces attention rather than a queue page.** `InitialPasswordAttentionIndicator` puts parked and expired counts on the Synchronisation Rules and Connected Systems lists, and the Synchronisation Rule's Passwords tab carries the per-reason breakdown. Requirements 21 and 22 ask for a page with per-event retry, which this store has no equivalent of; the indicator is a precedent for where to put the badge, not for the page itself.

### Divergences to carry forward

1. **Requirement 19's "must refuse to transmit unless the connection is LDAPS" was implemented as a warning, not a block.** `OpenPasswordConnection` logs a warning when the connection is unencrypted, and a refusal that an unencrypted connection would explain is classified with encryption named as the likely fix. It does not pre-empt the attempt, because a signed and sealed bind is a legitimate encrypted alternative that JIM cannot detect from the Connected System's settings alone, and blocking on the settings would refuse a valid configuration. The residual risk is real and should be closed by requirement 5's per-system "secure transport is mandatory" option rather than by a hard-coded rule: without it, a generic-LDAP password write over a plain connection puts the value on the wire before the directory has a chance to refuse it.
2. **`ActivityTargetOperationType.SetPassword` exists (value 12); the new Activity target *category* of requirement 24 does not.** Password events currently record against existing target types. Adding the category is still this feature's work, and the existing operation type should be reused rather than a second one introduced.
3. **JIM now stores a password value, which the Goals said it never would.** The Goals above allow no persisted cleartext password "other than the encrypted queue payload"; #1273 added a second such field, `SyncRuleInitialPassword.StaticPasswordEncryptedValue`, holding the one password a Synchronisation Rule sets on every account it provisions. It is a deliberate, administrator-chosen exception: write-only on every surface, reaching configuration change history as a keyed hash rather than a value, and recommended against in the portal beside the option. It is retired by delivering a generated password to whoever should have it ([#1252](https://github.com/TetronIO/JIM/issues/1252)), not by this feature. Nothing here weakens the rule for synchronised passwords, which are still queue-payload-only; the Goal's wording is simply now one exception out of date.
4. **That value is protected under the shared credential purpose, not a dedicated one.** Requirement 7 asks for the existing credential protection service under a purpose distinct from connector settings; `CredentialProtectionService` exposes one purpose (`JIM.Credentials.v1`) and `ProtectStaticPassword` uses it. Introducing the dedicated password purpose is still this feature's work, and doing so has to account for the static passwords already encrypted under the shared one.
5. **The per-system time to live now exists; adopt it rather than growing a second one.** `ConnectedSystem.InitialPasswordTimeToLive` is requirement 5's window, built under [#1316](https://github.com/TetronIO/JIM/issues/1316) and defaulting to Resolved Decision 4's seven days when unset. It is already the property of the Connected System this feature wants, so the queue should read the same field instead of adding one beside it.
6. **Closed (Phase 5).** The trim now runs on the built-in **History Retention Cleanup** Schedule, which absorbed the initial-password trim rather than leaving two mechanisms, and added the password queue trim beside it under `History.PasswordEventRetentionPeriod`. The housekeeping timer is gone; #1118 is delivered. Recorded as it stood: **The trim exists, in worker housekeeping rather than on a Schedule.** #1316 removes terminal `Parked` and `Expired` rows past `History.InitialPasswordRetentionPeriod` (90 days by default), beside the change-history trims and under the same batch cap. Requirements 28 to 30 ask for a Schedule instead, consistent with #1118; that was deliberately not built as a lone Schedule step ahead of #1118, so this feature's trim should absorb the existing one when it moves rather than leave two mechanisms. `InitialPasswordDeliveryServer.DeleteExpiredWorkRecordsAsync` is the call to move; the selection rules do not change.

### Delivered by the Password Synchronisation implementation

The plan at [`engineering/plans/done/PASSWORD_SYNCHRONISATION.md`](../../plans/done/PASSWORD_SYNCHRONISATION.md) built requirements 1 to 14, 21 to 30 and 32 to 33 across its six implementation steps. Recorded here as outcomes rather than as a list of files; the plan carries the detail.

| Req | State | Note |
|-----|-------|------|
| 1 to 5 (per-system configuration, the enable toggle, delivery options) | Done | `ConnectedSystemPasswordSynchronisation`, with the Passwords tab, REST resource and `Get-/Set-JIMConnectedSystemPasswordSynchronisation`. The time to live is `ConnectedSystem.InitialPasswordTimeToLive`, per divergence 5, rather than a second field. |
| 2 to 3 (accumulate while disabled, drain on enable) | Done, after a defect | Both were built in Phase 2, and the accumulate half was **wrong until [#1522](https://github.com/TetronIO/JIM/issues/1522)**: fan-out filtered its targets to enabled systems, so a configured-but-switched-off system was not a target and its changes were discarded rather than held. Delivery had always been correct, which is why it looked right by hand. Fan-out now targets configured systems and carries the enabled state through; queued changes for a switched-off system are reported as **held**, are excluded from the due count and from the worker's idle sweep, and are delivered when the system is enabled. Found by writing integration Scenario 20, which is what now guards it. |
| 6 to 8 (the encrypted queue, fan-out, coalescing) | Done | `PendingPasswordChange`, one row per (Metaverse Object, Connected System), coalesced in the database by a unique index plus `ON CONFLICT DO UPDATE`. Protected under the dedicated `JIM.PasswordSync.v1` purpose, closing divergence 4 for synchronised passwords. |
| 9 to 12 (expiry, retry with backoff, parking, failure classification) | Done | Adopting the initial-password work store's shape as this section recommended: delete on success, `Pending`/`Parked`, expiry recorded rather than swept, and `PasswordSetFailureReason` deciding between them. |
| 13 to 14 (never substitute a generated password; a change that reached nothing says so) | Done | There is no generator on this path at all. A change that found no target is still recorded as an Activity saying so, and the response reports it explicitly rather than as an empty list. |
| 21 to 23 (the queue page, per-change retry and cancel, Activities) | Done | Administration > Password Synchronisation, with retry and cancel over a single row or the whole filtered set, each recorded as one Activity. |
| 24 (a new Activity target category) | Done | `ActivityTargetType.PasswordSynchronisation`, closing divergence 2; the existing `SetPassword` operation type is reused as that divergence required. |
| 25 to 27 (the Metaverse Object panel, the Connected System list indicator) | Done | The panel reads a person's history from Activities rather than from the queue, because a delivered change leaves the queue and a queue-only view would show a person's failures and none of their successes. |
| 28 to 30 (retention, on a Schedule) | Done | The built-in **History Retention Cleanup** Schedule, which absorbed the initial-password trim rather than leaving two mechanisms. Closes divergence 6 and delivers [#1118](https://github.com/TetronIO/JIM/issues/1118). |
| 32 to 33 (parity across the three surfaces) | Done | Configuration and queue read, retry and cancel all exist on the portal, the REST API and PowerShell. |
| 34 (secure-transport-only password endpoints) | Done | Closing the partial state recorded above: every password-accepting endpoint now refuses a request whose transport JIM cannot confirm is encrypted, with a build-time guard against a new one being added without the check. |

### Not started

Requirement 16's warning still has no call site outside the Synchronisation Rule Attribute Flow warning added under this feature; see Requirements partially satisfied. Phase 2 inbound capture (the ingress API and the Domain Controller capture agent) is untouched and gets its own plan. Inbound password mapping on import is no longer part of it; see [#1563](https://github.com/TetronIO/JIM/issues/1563).

## Amendment (2026-09-05): one pipeline, dedicated delivery, Operations

Three decisions taken after Phase 1 shipped under Unreleased, recorded here because they change requirements this PRD stated. Implementation plan: [`engineering/plans/done/PASSWORD_PIPELINE_CONVERGENCE.md`](../../plans/done/PASSWORD_PIPELINE_CONVERGENCE.md), issue [#1635](https://github.com/TetronIO/JIM/issues/1635).

1. **Set Password and Synchronise Password converge.** Requirement 31's synchronous set-password and this PRD's queued change end in the same connector call on the same accounts; what separated them was which accounts and whether JIM kept trying. There is now one **Set Password** operation over the queue, with a target mode: named accounts, or every Connected System configured for Password Synchronisation. Synchronise Password is withdrawn from the portal, REST and PowerShell before release. Explicit resets carry the account id on the row, deliver to a system with no configuration using default retry settings, and deliver even where Password Synchronisation is switched off (the administrator named the account); propagated changes behave as requirements 1 to 14 describe. Consequence for requirement 31: a set password is no longer "written straight to the system and stored nowhere"; it is held encrypted until delivered, which for a reachable system is the duration of the write, and kept on refusal so JIM can finish the job.
2. **Delivery leaves the worker task queue.** Requirement 21's "Password Delivery task" was a `WorkerTask`, and the worker dispatches nothing new while any task is running; retries were caught by the worker's idle tick. Delivery now belongs to a dedicated Password Delivery Service, hosted in `jim.worker` as its own loop, woken by a database notification when a row is queued, claiming rows per Connected System. The latency promise becomes: first attempt within a second of queueing, independent of synchronisation activity. Callers may wait briefly for outcomes.
3. **The queue page moves under Operations, and Operations reports service health.** Requirement 21's page becomes the **Passwords** tab of Administration > Operations, beside the sync queue, history and schedules, because it is the same job for an administrator: work in flight and what happened to it. Operations gains a Service Health strip from heartbeats each service writes to the database, so an administrator can tell whether the Worker is running and making progress without a container console.

Requirements 25 (the person's panel) and 31 (set on all three surfaces) are satisfied by the converged operation; requirement 21's page by the Operations tab. Acceptance criteria above that name the standalone page or the worker task should be read through this amendment.

## Acceptance Criteria

Every criterion below is met. Each carries where it lives and what proves it, so the claim can be checked without reading the whole diff. "Scenario 20" is `test/integration/scenarios/Invoke-Scenario20-PasswordSynchronisation.ps1`, run green against a live Samba AD domain controller (23 assertions).

- [x] Password Synchronisation can be configured, enabled, and disabled per Connected System from the portal, REST API, and PowerShell. **Evidence:** `ConnectedSystemPasswordSynchronisationTab.razor`; `GET`/`PUT /connected-systems/{id}/password-synchronisation`; `Get-/Set-JIMConnectedSystemPasswordSynchronisation`. Proven by `SynchronisationControllerPasswordSynchronisationTests` and `PasswordSynchronisationConfigurationValidationTests`. The tab is hidden, not disabled, where the Connector lacks the capability (requirement 4).
- [x] A password change fans out to all enabled configured systems and is delivered through the connector password capability. **Evidence:** `PasswordSynchronisationServer.QueuePasswordChangeAsync` and `PasswordDeliveryWorkerTask`. Proven by `PasswordSynchronisationFanOutTests` (`QueuesOneChangePerEnabledSystem`, `NeverQueuesForASystemNobodyConfigured`, `IgnoresAccountsOfAnotherObjectType`, `ForASystemTheIdentityHasNoAccountIn_StillQueues`) and `PasswordDeliveryTests.Deliver_SendsTheDecryptedPasswordToTheConnector`.
- [x] The LDAP connector sets passwords in Active Directory mode over LDAPS and refuses to transmit over an unencrypted connection. **Evidence:** `LdapConnectorPassword.cs`, with the refusal decided by `PasswordChannelSecurity.RefusesChannel` from the Connector's `IsPasswordChannelSecure`. Proven by `LdapConnectorPasswordTests`, `PasswordChannelSecurityTests`, and `PasswordDeliveryPassTests.DeliverDueAsync_SecureTransportRequiredAndChannelIsNot_DeliversNothing`.
- [x] Queued events coalesce to the latest password per (Metaverse Object, Connected System) and never replay a historical sequence. **Evidence:** A unique index plus `ON CONFLICT ... DO UPDATE` in `SyncRepository.PasswordOperations.cs`, so last-write-wins is atomic in the database rather than a read-modify-write. Proven against real PostgreSQL by `PasswordSynchronisationQueueDatabaseTests` (`ForTheSameTargetTwice_CoalescesToOneRow`, `WithTwoChangesForOneTargetInOneBatch_Coalesces`, `SupersedingAParkedChange_ClearsItsFailureHistory`) and end to end by Scenario 20, which sends three passwords and signs in with the third.
- [x] Events expire after their time-to-live with an explicit recorded outcome. **Evidence:** `PendingPasswordChangeStatus.Expired`, set by the delivery pass before it attempts anything. Proven by `PasswordDeliveryTests.Deliver_ExpiresOverdueChangesBeforeAttemptingAnything` and `PasswordSynchronisationQueueDatabaseTests.ExpirePasswordChangesAsync_LeavesParkedChangesAlone`.
- [x] A disabled Connected System accumulates events and drains them automatically on enable. **Evidence:** Requirement 2 and 3. This was **wrong until [#1522](https://github.com/TetronIO/JIM/issues/1522)** and is now guarded on both halves: `PasswordSynchronisationFanOutTests.QueuePasswordChange_ForAConfiguredButDisabledSystem_StillQueues` for accumulate, `ConnectedSystemPasswordSynchronisationDrainTests.PasswordSynchronisationTurnedOn_ReleasesParkedChanges` for drain, and Scenario 20 for both without intervention.
- [x] Failures are retried with backoff, are visible with error detail on the queue page, and can be retried manually. **Evidence:** Proven by `PasswordDeliveryTests` (`WithATransientFailure_KeepsTheChangeAndSchedulesARetry`, `WithAPolicyRejection_ParksWithTheTargetsOwnWords`, `WhenTheAccountDoesNotExistYet_RetriesRatherThanParking`), `PasswordDeliveryHousekeepingTests` for the due-retry sweep, and `PasswordSynchronisationControllerQueueTests` for retry and cancel over one row or the whole filtered set. Manual retry is `Resume-JIMPendingPasswordChange` and the queue page's Retry action.
- [x] Every password event produces an Activity with per-target outcomes, filterable by the new Activities category and by Connected System. **Evidence:** `ActivityTargetType.PasswordSynchronisation` (divergence 2), one child Activity per target outcome. Proven by `PasswordSynchronisationEventDatabaseTests` and `PasswordDeliveryTests.Deliver_RecordsTheOutcomeAttributedToTheSystem`, which also pins the outcome to the System initiator rather than to nobody ([#1529](https://github.com/TetronIO/JIM/issues/1529)).
- [x] The Metaverse Object detail page shows password events for that identity to administrators only. **Evidence:** Read from Activities rather than from the queue, because a delivered change leaves the queue and a queue-only view would show a person's failures and none of their successes. Proven by `MetaverseObjectPasswordSynchronisationPanelTests`.
- [x] The Connected System list indicates, filters, and sorts on Password Synchronisation state. **Evidence:** `ConnectedSystemList.razor`, with a `passwordsync` sort arm and a filter distinguishing four states (no capability, unconfigured, configured but off, delivering) plus a Needs Attention cut. Proven by `PasswordSynchronisationIndicatorTests` and `PasswordSynchronisationStateDatabaseTests`.
- [x] A Schedule trims terminal queue rows and expired password Activities under their own retention setting. **Evidence:** The built-in daily **History Retention Cleanup** Schedule and `History.PasswordEventRetentionPeriod`, one period governing both so a queue row and its outcomes never age apart. Proven by `PasswordSynchronisationQueueDatabaseTests.DeleteTerminalPasswordChangesAsync_NeverRemovesLiveWork`; delivers [#1118](https://github.com/TetronIO/JIM/issues/1118).
- [x] Well-known credential attributes cannot be selected for import or targeted by an Attribute Flow, and a credential-like attribute name outside the denylist raises a configuration warning. **Evidence:** `CredentialAttributes.IsCredentialAttribute` blocks selection on the Schema tab and at the API, and blocks Attribute Flow targeting and Scoping Criteria; `CredentialAttributes.HasCredentialLikeName` raises the warning from `SyncRule.Validate()`. **Scope worth knowing:** the warning has exactly that one call site, which is the one this criterion asks for; requirement 16's wider ambition of warning wherever an attribute is named is not delivered (see Not started).
- [x] No password value appears in any log, Activity, change record, preview, DTO, or API response; verified by test and by security review. **Evidence:** The queue DTO has nowhere to put a payload; the value is protected under the dedicated `JIM.PasswordSync.v1` purpose. Proven by `PasswordProtectionPurposeTests`, `PasswordEndpointSecureTransportTests`, `PasswordDeliveryPassTests.DeliverDueAsync_Delivered_DescribesTheOutcomeWithoutNamingAPassword`, `Get-JIMRedactedBody` with `ApiBodyRedaction.Tests.ps1`, and Scenario 20's sweep of the containers' own logs for every password value it sent. The security review found and closed two real leaks: [#1516](https://github.com/TetronIO/JIM/issues/1516) (PowerShell writing the API key to the debug stream) and unredacted request bodies in `Invoke-JIMApi`.
- [x] Unit tests cover fan-out, coalescing, expiry, retry, enable-drain, and the never-log invariant; integration tests cover end-to-end delivery to a directory. **Evidence:** The fixtures named against each criterion above, plus `PasswordSynchronisationQueueDatabaseTests` and `PasswordDeliveryReadsDatabaseTests` against real PostgreSQL. End to end is Scenario 20, which drives queued-while-off, held, switched on, delivered unaided, and proves the directory answers the new password and refuses the old.
- [x] Public documentation ships in the same pull request. **Evidence:** [`docs/concepts/passwords.md`](https://docs.junctional.io/concepts/passwords/) and [`docs/powershell/password-synchronisation.md`](https://docs.junctional.io/powershell/password-synchronisation/), with the Activities category, Connected Systems, LDAP connector and History pages updated alongside. `changelog-lint` enforces the pairing on every user-facing entry.

## Additional Context

### Phasing

**Phase 1: JIM as password origin.** Everything in this document: configuration, queue, connector capability, delivery, reporting, audit, retention, and the three surfaces. Password changes originate from an administrator in the portal, from the REST API, from PowerShell, or from provisioning.

**Phase 2: inbound capture.** One channel into the password channel, having been two. The second is recorded below with the reason it was carved out, because the reasoning is worth keeping:

- *Ingress API.* A documented, API-key-authenticated endpoint that an external capture agent posts password change events to, with payload envelope encryption so that a TLS-terminating proxy cannot recover the password, a versioned wire contract, replay protection, and per-agent check-in reporting so an administrator can see which capture agents are healthy.
- *Inbound password mapping on import.* **Carved out to [#1563](https://github.com/TetronIO/JIM/issues/1563) and no longer part of Phase 2.** It would have let a per-Connected-System setting nominate an imported attribute as a password, diverted at the import boundary. Two reasons for parking it: nothing has asked for it, and it is the only channel in this feature that is not event-shaped. An import reports current state rather than what changed, so the nominated column arrives again on every run holding the same value, and JIM cannot tell a genuine change from a repeat without keeping something derived from the password, which is the one thing the rest of the design refuses to do. The issue carries the design and the three questions that would need settling.

**Phase 3 and beyond (not committed):** the native Domain Controller capture agent; a SCIM inbound password channel; self-service password reset; defensive password filtering ([#1120](https://github.com/TetronIO/JIM/issues/1120), see below).

### Future follow-on: defensive password filtering (not v1)

> Tracked as [#1120](https://github.com/TetronIO/JIM/issues/1120), dependent on this feature.

Once the capture path exists, the same event stream can be evaluated for password strength, so JIM can decline to propagate a weak or breached password and record why. This is a natural extension, not part of Phase 1, and it comes in two distinct forms that must not be conflated because they live in different places and give different guarantees:

- **Refuse-to-propagate (the JIM-side feature).** JIM receives a captured or administrator-supplied password, evaluates it against a configurable policy (length, complexity, a breached-password list; an air-gap-friendly local corpus rather than an online service, per the self-contained constraint), and if it fails, does not synchronise it onward, marking the event with a distinct "rejected: weak password" outcome and alerting. This fits the queue, Activity, and reporting model already in this PRD, works for every origin, and is the version JIM would build. Its limit is honest: the password has usually *already been accepted* by the originating directory, so JIM is preventing its *spread*, not its *existence*.
- **Reject-at-source (lives in the DC agent, if ever).** The agent's filter DLL returns false from `PasswordFilter`, blocking the weak password in Active Directory itself before it is ever set. This is what OpenPasswordFilter does, and it is the only form that actually prevents a weak password existing. It requires the policy and breached-list to be evaluated locally on every Domain Controller (a synchronous call out to JIM from inside LSASS is not acceptable), which is a materially larger and riskier undertaking bound to the agent's own lifecycle.

The pragmatic path is refuse-to-propagate as the JIM feature, with reject-at-source considered only as an agent capability much later. Worth capturing now so the queue's outcome enum and the Activity model leave room for a "rejected by policy" terminal outcome, rather than being retrofitted.

### Prior art: can we adopt an existing password filter?

**No existing component can be adopted wholesale, but the hardest architectural problem is well-solved in public and should be used as reference rather than re-derived.** A Windows password filter is a documented OS extension point (the `Notification Packages` mechanism), so the DLL skeleton, the LSASS registration, and the `PasswordChangeNotify` callback are the same in every implementation; the differentiator is what happens after the callback fires. Public projects fall into three groups:

- **Defensive filters (e.g. OpenPasswordFilter and its forks).** Mature and widely deployed, but they do the opposite job: they *reject* weak or breached passwords at change time, they do not forward them. Their value to us is purely architectural: they demonstrate the mandatory split (a minimal native DLL in LSASS talking to a userspace service over local IPC) working in production. They are typically GPL-family licensed, which is incompatible with shipping a binary agent under the Tetron Commercial Licence, so code reuse (as opposed to design reference) is off the table under the dependency governance rules.
- **Managed-forwarding filters (e.g. `ManagedPasswordFilter`).** The closest architectural analogue: a thin native DLL that hands the account name and new password to a managed (.NET) worker process, which is exactly the shape our agent needs. Useful as a reference for the native-to-managed marshalling boundary. It does not solve delivery, durability, encryption, signing, or packaging, which is where the actual cost of our agent lies.
- **Offensive/exfiltration filters (e.g. GoSecure's `DLLPasswordFilterImplant`).** These *do* capture and forward the cleartext password, so they prove the exact capture-and-send mechanism we need, but they are built as red-team implants: no durability, no operational security, no signing, and adopting attacker tooling into a product shipped to healthcare and government is a non-starter on both trust and licence grounds. Reference only, and even then chiefly to understand what a customer's blue team will look for when they audit our agent.

**Conclusion: build our own, using these as design references, not dependencies.** The reusable insight, the LSASS split and the `PasswordChangeNotify` marshalling, is the easy 20%; the 80% that determines whether the agent is fit for JIM's sectors (signing under LSA Protection, durable encrypted local queueing, delivery, packaging, coverage monitoring, and an independent security assessment) is exactly the part no public project provides. There is no shortcut here that survives contact with the prerequisites below.

### Phase 2 prerequisites: the Domain Controller capture agent

The agent is not a JIM feature so much as a separate product with its own supply chain. Prerequisites, roughly in order of lead time:

- **Signing under LSA Protection.** A Windows password filter is a native DLL loaded into LSASS, and where a customer has enabled LSA protection (RunAsPPL), LSASS loads only plugins carrying a Microsoft signature. JIM's target sectors are exactly the ones that enable it, so the agent is not deployable without this. **Checked against Microsoft's current documentation, and it is a submission service rather than an approval gate**, which is a smaller obstacle than this section previously claimed: Microsoft states explicitly that LSA plugins do **not** go through Windows Hardware Lab Kit qualification (that is for drivers), and the shim review board that reviews submitted source applies to UEFI firmware only, not to LSA plugins. The route is:
    1. Register Tetron Limited for the Windows Hardware Developer Program on the Partner Center hardware dashboard.
    2. Obtain an extended-validation code-signing certificate for Tetron from a trusted CA.
    3. Package the DLL as a single signed CAB, binaries only and no folders, whose signature matches that EV certificate.
    4. Submit it under **File Signing Services → Submit New LSA** in Partner Center, accepting the one-off legal agreement.
    5. Download the Microsoft-signed package.

    The real lead time is therefore the EV certificate and the program registration, both of which the agent needs anyway. Two residual risks are worth stating rather than assuming away. First, documented: plugins must conform to Microsoft's Security Development Lifecycle process guidance, and Microsoft says non-conformance can cause a plugin to fail to load *even when correctly signed*, so this is an ongoing obligation and not a one-off gate. Second, undocumented and therefore an assumption to test: a filter that captures cleartext passwords and forwards them off the host is structurally what a credential-exfiltration implant looks like, and while no review step is published for LSA plugins, there is no published service level or appeal route either. **Decided: do not pre-contact Microsoft about this.** The process is self-service from end to end and the question has no obvious addressee, so establishing one and waiting on an answer costs more than the risk it retires. Submit through the documented route and find out. If a submission is refused, that is the point at which there is something concrete to ask about.
- **Code-signing certificate: extended validation, from one of six named authorities.** EV is what the LSA submission requires, and the same certificate covers the DLL, the service binary and the installer. Microsoft accepts EV certificates for the hardware dashboard only from Certum, DigiCert, GlobalSign, IdenTrust, Sectigo or SSL.com. The route:
    1. Buy EV from one of those six. Certum and SSL.com sit at the cheaper end; DigiCert and GlobalSign are the enterprise defaults.
    2. Complete EV validation of Tetron Limited: the authority verifies the legal entity against public records, the registered address, a verifiable phone listing, and the identity of an authorised representative. **Confirm Companies House details are current before starting**, because stale public records are the documented main cause of delay.
    3. **Take the cloud signing option, not the USB token.** Since June 2023 the CA/Browser Forum requires code-signing private keys to live on FIPS 140-2 Level 2 or better hardware, and every authority offers both a shipped dongle and a cloud service. A dongle needs a person physically attaching hardware to whichever machine builds and signs the CAB, which does not work in CI, and switching later means re-issuing the certificate.
    4. Then create the Partner Center hardware dashboard account and register the certificate against it. That order is deliberate: an already-approved EV certificate can be used to establish the account.
    5. Sign with `signtool /fd sha256`, and timestamp so signatures outlive certificate expiry.

    Allow two to three weeks, nearly all of it validation, and note that it is self-service throughout. **Azure Artifact Signing (formerly Trusted Signing) does not substitute**, despite being far cheaper and built for CI/CD, and despite Tetron being eligible for it as a UK organisation: Microsoft positions it as an alternative to *organisation-validated* certificates, and the hardware dashboard requires EV from the list above. Worth starting regardless of whether the agent is built, because it is the long pole and it is needed for any signed Windows binary JIM ships.
- **Split-process architecture, non-negotiable.** The filter DLL is called synchronously by LSASS on every password change; a fault kills LSASS and takes the Domain Controller down, and latency there slows every password change in the domain. The DLL must therefore do almost nothing: hand the change to a local Windows service over local IPC, and let that service own durable queueing, encryption, retry, and network delivery.
- **Toolchain: two components, two separate decisions.** The agent is a filter DLL plus a userspace service, and only the first is constrained.

    **The DLL is constrained, and the constraints are documented.** Microsoft requires it to export `InitializeChangeNotify`, `PasswordFilter` and `PasswordChangeNotify` with the `__stdcall` calling convention from a DLL registered in `Notification Packages`; to be thread-safe; to treat the supplied buffers as read-only; and to clear the plaintext password from memory with `SecureZeroMemory` when finished. It runs as Local System, `PasswordChangeNotify` blocks the password change while it executes, and Microsoft states plainly that *"any process exception that is not handled within this function may cause security-related failures system-wide"*. This is the same architecture Microsoft's own MIM password change notification service uses (`Pcnsflt.dll` plus a service), which is worth knowing: the shape is proven, not novel.

    **Can it be .NET?** Technically yes, and the previous wording here ("the CLR cannot be hosted inside LSASS") was imprecise, because NativeAOT emits a genuinely native DLL with no CLR to host and `[UnmanagedCallersOnly]` can export the entry points. It should still be rejected, for two reasons specific to this component rather than to .NET. The `SecureZeroMemory` requirement is the decisive one: a garbage-collected runtime may copy and relocate the plaintext password before anything scrubs it, so the guarantee Microsoft asks for cannot honestly be given. Second, a managed exception escaping the boundary is exactly the "security-related failures system-wide" case, and the NativeAOT runtime is a large amount of machinery to justify under SDL review for a component whose whole job is to hand a string to a local service.

    **Technology is therefore TBD between the native options, and Rust is to be evaluated against C and C++.** The case for Rust is specific: a memory-safety fault here is not a code-quality nuisance, it is a domain-wide outage, and the buffer-overflow and use-after-free class has produced real CVEs in C password filters. Rust removes that class at compile time while emitting a plain `cdylib` with the required C-ABI exports, and has mature Microsoft-maintained Windows bindings (the `windows` crate). Caveats to carry into the evaluation: compile with `panic = "abort"` (or wrap every entry point in `catch_unwind`) so a panic can never unwind into LSASS; keep `unsafe` confined to a thin Win32 interop shim; confirm that scrubbing can be guaranteed (`zeroize` and friends) as rigorously as `SecureZeroMemory`; and note that the language changes nothing about the signing requirement above, which is language-agnostic. The genuine cost is team familiarity, weighed against a small, sharply-bounded component that makes a sound first Rust footprint rather than a risky one.

    **The service is unconstrained.** It does not run inside LSASS, so it is free to be .NET, matching the rest of JIM's stack, tooling and team. It owns everything that actually carries risk over time: durable queueing, encryption, retry and network delivery.

    Alongside whichever toolchain: Windows build agents, a signed MSI built with a proper installer toolchain, upgrade codes, and a clean uninstall path.
- **Local durability.** A DPAPI-machine-key-encrypted local queue with a cap, a time-to-live, and backoff, so a JIM outage does not lose changes and a long outage does not fill the Domain Controller's disk.
- **Deployment and coverage monitoring.** The agent must be installed on every writable Domain Controller or changes processed elsewhere are silently missed; registration requires a reboot. JIM should therefore report agent check-ins and alert on a Domain Controller that stops reporting.
- **A real Active Directory lab.** Multi-Domain-Controller, across supported Windows Server versions. This cannot be validated in the development container or the cloud sandbox.
- **Independent security assessment.** A component that sees every cleartext password in the domain will be threat-modelled and penetration-tested by customers; commissioning that ourselves first is cheaper than discovering it in a customer's review.
- **Its own repository, public, under the Tetron Commercial Licence.** Decided: the agent gets a dedicated repository rather than living in `TetronIO/JIM`, following the precedent already set by `JIM-Brain` and `JIM-Bench`. The reasons are concrete. `Directory.Build.props` stamps every assembly from the root `VERSION` file, and the agent must version independently, because it is deployed on Domain Controllers, upgraded on the customer's schedule, and stays wire-compatible across several JIM releases (which is why the ingress contract is versioned). JIM's CI runs on every pull request and already carries self-hosted tests that must be serialised; adding Windows runners and a native toolchain would tax every unrelated change. The agent's release pipeline (EV signing, CAB submission, MSI) shares nothing with the server's. And a customer's security team auditing a DLL that runs inside LSASS should be able to clone that component alone rather than the whole product. **The repository name is to be decided, but must identify it as Active Directory Password Synchronisation rather than being generically an "agent".** Public source under the commercial licence, as JIM itself already is: for a component that sees every cleartext password in the domain, inspectability is the point, and secrecy buys nothing when Microsoft documents the mechanism and public implementations already exist.
- **Licensing and redistribution terms** for a distributed binary agent, distinct from the server licence.

Two mitigations worth weighing against that cost. First, the agent is only needed where the directory is the password master; customers whose password master is the identity provider, or who adopt a future self-service reset feature, need only Phase 1 and Phase 2. Second, the Windows notification-package registration accepts multiple filters, so JIM's agent can be installed alongside an incumbent product's during a parallel-run migration, which materially de-risks a migration cutover. Note that an incumbent capture agent cannot simply be repointed at JIM: those agents speak a proprietary protocol to their own synchronisation service, so a migration necessarily involves deploying ours.

### Why credential attributes are denylisted

Nothing in JIM today stops an administrator importing a cleartext password into a plain text attribute and mapping it, via an Attribute Flow, to export as `unicodePwd` or `userPassword`. The LDAP connector already carries `byte[]` attribute values end to end, and the only existing attribute guard (the protected-attribute defaults list) is about *clearing* attributes in AD, not credentials. So the do-it-yourself route works, and some administrators will reach for it the moment they see JIM can export to a directory. We should treat that as a hazard to close off, not a feature to rely on, for three reasons:

- **It persists the secret in the clear throughout the Metaverse.** A password mapped as an attribute is stored as a Connected System Object attribute value and a Metaverse Object attribute value, written into CSO and MVO change history, materialised as a Pending Export, shown in export previews, returned by search and the API, and captured in every database backup. It is exactly the exposure this whole feature exists to avoid, reintroduced by the back door.
- **The `unicodePwd` write will usually fail anyway.** AD only accepts `unicodePwd` as a quoted UTF-16LE value over LDAPS with modify semantics that the generic export path does not produce, so the DIY mapping tends to *look* configured while silently never setting a password, which is worse than an honest refusal.
- **It has no queue, no retry, no audit-without-the-value, and no coalescing.** It bypasses everything Phase 1 provides.

Hence requirements 14 and 15: well-known credential attributes are denylisted from import and from Attribute Flow selection, and a credential-like attribute name outside that list raises a configuration warning pointing the administrator at the password channel. The denylist is not a security boundary against a determined administrator (they own the schema and could rename an attribute), it is a guardrail that makes the safe path the obvious one and the dangerous path deliberate.

### Rejected and deferred inbound channels

**Traditional ILM systems have no file-based password channel, and neither should Phase 1.** Their inbound password path is exclusively capture-at-change push from a Domain Controller agent, plus a programmatic interface on the synchronisation engine for administrative set and reset. Passwords are not part of import runs, and there is no password column in any import. The industry pattern is consistent across the major products: a directory password filter pushing to the synchronisation engine. Nothing in that landscape requires JIM to accept passwords in a file for migration parity, so a CSV channel is not a migration blocker.

It is worth noting how closely the incumbent model matches what this PRD proposes, which is reassuring for migrating operators: password synchronisation is configured per Management Agent rather than per rule, there is an explicit enable toggle, targets implement a password interface distinct from the export interface, per-target retry count and interval are configurable, secure transport can be mandated, and password synchronisation history is retained and queryable. JIM's differentiators are that the queue and history are first-class objects in the portal with manual retry, rather than being reachable only through a management interface.

A file-based bulk initial-password load has genuine uses (bulk onboarding, one-way transfer into a high-side network, migration cutover) but is deferred rather than designed in, because cleartext passwords sitting in a file are a serious exposure: readable by backup agents, replicated by file sync, and impossible to attribute. If it is ever built, the minimum controls are envelope encryption to a JIM-held public key so a file at rest is useless on its own, consume-and-shred semantics, forced change-at-next-sign-in, per-file audit, and an explicit opt-in setting. The better answer to "an inbound channel that is not our own API" is a SCIM inbound path, where `password` is a standard write-only attribute, which fits the existing post-MVP SCIM plan and is a standard rather than a bespoke file format.

### Design note: why passwords do not use the export pipeline

The export pipeline persists per-attribute change values, surfaces them in previews and change history, retries from that persisted state, and expects confirmation by re-import. Every one of those behaviours is wrong for a secret that must be write-only, unreadable, and unconfirmable. Reusing Pending Exports would put cleartext passwords into export previews and change records, and would leave the pipeline waiting forever for a confirmation that can never arrive. The parallel channel is more work than reusing the existing rails and is the only correct option.
