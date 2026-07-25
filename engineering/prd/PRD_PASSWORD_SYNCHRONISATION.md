# Password Synchronisation

- **Status:** Planned
- **Created:** 2026-07-25
- **Author:** Tetron
- **Issue:** [#1119](https://github.com/TetronIO/JIM/issues/1119)

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
- **Keycloak as a provisioning target.** Keycloak remains JIM's authentication provider, not a Connected System.
- **The Domain Controller capture agent itself.** Phase 2 delivers the inbound ingress API that such an agent would call. The native Windows agent is a separate deliverable with its own toolchain, signing, and release cycle; see "Phase 2 prerequisites" below.
- **File-based (CSV) password ingest.** Deliberately deferred; see "Rejected and deferred inbound channels".

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
13. A password change that targets zero enabled systems is still recorded as an Activity, with an outcome that makes the no-op explicit.

**Connector capability**

14. A new capability interface, provisionally `IConnectorPasswordManagement`, exposes a set-password operation against a Connected System Object, plus open and close semantics consistent with the existing export capability interfaces.
15. A corresponding capability flag is added to the connector capability contract and mirrored on the persisted Connector Definition.
16. The LDAP connector implements the capability: Active Directory mode writes `unicodePwd` using the required quoted UTF-16LE encoding and **must refuse to transmit unless the connection is LDAPS**; generic LDAP mode writes `userPassword`. The mode is selected by a per-system setting.
17. The connector supports an initial-password-on-create flow and an optional "user must change password at next sign-in" behaviour where the target system has such a concept.

**Reporting and audit**

18. A Password Synchronisation queue page lists queued, failed, and expired events with target Connected System, target object, status, error detail, attempt count, and next retry time. It never displays, and its backing DTO never carries, the password value.
19. The queue page supports manual retry of a single event and of a filtered selection, and supports cancelling or deleting an event.
20. Every password change event produces an Activity: a parent Activity for the change and a child outcome per target Connected System recording success or failure.
21. A new Activity target type is added for Password Synchronisation, mapped to a **new Activity target category** so the existing Activities list quick-filter isolates password events with no new filter controls. Per-Connected-System filtering comes for free from the existing filter options provided the Activity sets its target context to the Connected System name.
22. The Metaverse Object detail page gains a Password Synchronisation panel showing that identity's recent password events and their per-system outcomes. The panel is visible to administrators only.
23. The Connected System list view indicates Password Synchronisation state per row, and supports filtering and sorting on it.
24. Queue rows and their Activities are reconciled: a queue row links to the Activity that created it, and the Activity records the terminal outcome. The queue holds operational state only; the Activity is the durable audit record and outlives the queue row.

**Retention**

25. The Pending Password Change queue is trimmed automatically by a Schedule, not by worker housekeeping, consistent with the direction of #1118. Terminal-state rows older than a retention period are removed; live rows are never trimmed.
26. Password event Activities are retained under their own retention class and Service Setting, alongside the existing general, configuration-change, and security-event retention periods.
27. Trim operations are batched using the existing cleanup batch-size setting and report summary statistics.

**Surfaces**

28. Setting a password is available on all three surfaces: a portal dialog, a REST endpoint, and a PowerShell cmdlet.
29. Password Synchronisation configuration (create, read, update, enable, disable) is available on all three surfaces.
30. Queue read and manual retry are available on all three surfaces.
31. REST endpoints that accept a password are administrator-only, are excluded from request logging, and reject the request unless the transport is secure.

### Non-Functional Requirements

- A password value exists in cleartext only in process memory and only for the duration of a delivery attempt. At rest it is always encrypted under the dedicated protection purpose.
- No code path may log, sanitise-and-log, include in an exception message, include in an OpenAPI example, or serialise a password into any DTO returned to a client.
- Fan-out and queue writes must not materially slow the enclosing operation at customer scale; a password change for one identity across ten Connected Systems is a single batched write.
- Queue processing must tolerate a Connected System being unavailable for an extended period without unbounded growth, via coalescing, time-to-live, and scheduled trim together.
- The feature must work air-gapped, with no cloud key service.
- The queue must survive a worker restart with no lost or duplicated deliveries.

## Examples and Scenarios

### Scenario 1: Provisioning sets an initial password

**Given**: A Connected System "Corporate AD" has Password Synchronisation configured and enabled, and a Synchronisation Rule provisions new joiners into it.
**When**: A new identity is provisioned and an initial password is generated.
**Then**: The account is created, the password is set on the new account over LDAPS, "user must change password at next sign-in" is applied, the queue row is deleted on success, and an Activity records a successful password set against "Corporate AD" with no password value anywhere in the record.

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
| Connectors | New password capability implemented by the LDAP connector (AD `unicodePwd` and generic `userPassword` modes, LDAPS enforcement); Connector Factory wiring; Mock connector support for testing |
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

## Open Questions

1. Should the fan-out scope be every Connected System with Password Synchronisation enabled that the identity has a Connected System Object in, or should it additionally honour a scoping filter? Starting position: all linked, enabled systems, with scoping deferred until asked for.
2. Should a password change also be deliverable to a system where the identity has no Connected System Object yet (queue until provisioned), or fail immediately? Starting position: queue until the object exists, bounded by the event time-to-live.
3. Should the initial-password generator be an expression, a policy object, or both? Starting position: reuse the existing expression engine.
4. What is the default event time-to-live? Starting position: 7 days, configurable per Connected System.

## Acceptance Criteria

- [ ] Password Synchronisation can be configured, enabled, and disabled per Connected System from the portal, REST API, and PowerShell.
- [ ] A password change fans out to all enabled configured systems and is delivered through the connector password capability.
- [ ] The LDAP connector sets passwords in Active Directory mode over LDAPS and refuses to transmit over an unencrypted connection.
- [ ] Queued events coalesce to the latest password per (Metaverse Object, Connected System) and never replay a historical sequence.
- [ ] Events expire after their time-to-live with an explicit recorded outcome.
- [ ] A disabled Connected System accumulates events and drains them automatically on enable.
- [ ] Failures are retried with backoff, are visible with error detail on the queue page, and can be retried manually.
- [ ] Every password event produces an Activity with per-target outcomes, filterable by the new Activities category and by Connected System.
- [ ] The Metaverse Object detail page shows password events for that identity to administrators only.
- [ ] The Connected System list indicates, filters, and sorts on Password Synchronisation state.
- [ ] A Schedule trims terminal queue rows and expired password Activities under their own retention setting.
- [ ] No password value appears in any log, Activity, change record, preview, DTO, or API response; verified by test and by security review.
- [ ] Unit tests cover fan-out, coalescing, expiry, retry, enable-drain, and the never-log invariant; integration tests cover end-to-end delivery to a directory.
- [ ] Public documentation ships in the same pull request.

## Additional Context

### Phasing

**Phase 1: JIM as password origin.** Everything in this document: configuration, queue, connector capability, delivery, reporting, audit, retention, and the three surfaces. Password changes originate from an administrator in the portal, from the REST API, from PowerShell, or from provisioning.

**Phase 2: inbound capture ingress.** A documented, API-key-authenticated endpoint that an external capture agent posts password change events to, with payload envelope encryption so that a TLS-terminating proxy cannot recover the password, a versioned wire contract, replay protection, and per-agent check-in reporting so an administrator can see which capture agents are healthy.

**Phase 3 and beyond (not committed):** the native Domain Controller capture agent; a SCIM inbound password channel; self-service password reset.

### Phase 2 prerequisites: the Domain Controller capture agent

The agent is not a JIM feature so much as a separate product with its own supply chain. Prerequisites, roughly in order of lead time:

- **Signing under LSA Protection is the go/no-go item.** A Windows password filter is a native DLL loaded into LSASS. Where a customer has enabled LSA protection (RunAsPPL), LSASS will only load plugins meeting the protected-process signing requirements, which for a third party means a Microsoft-issued signature obtained through the Microsoft hardware developer signing programme rather than an ordinary code-signing certificate. This has a long lead time and needs confirming with Microsoft before any engineering commitment, because JIM's target sectors are exactly the ones that enable LSA protection.
- **Code-signing certificate.** Organisation-validated or extended-validation certificate for Tetron Limited, with the private key on FIPS-compliant hardware or a cloud signing service, plus timestamping so signatures outlive certificate expiry. Required for the DLL, the service binary, and the installer.
- **Split-process architecture, non-negotiable.** The filter DLL is called synchronously by LSASS on every password change; a fault kills LSASS and takes the Domain Controller down, and latency there slows every password change in the domain. The DLL must therefore do almost nothing: hand the change to a local Windows service over local IPC, and let that service own durable queueing, encryption, retry, and network delivery.
- **Native toolchain and CI.** C or C++ (no CLR in LSASS), Windows build agents, a signed MSI built with a proper installer toolchain, upgrade codes, and a clean uninstall path.
- **Local durability.** A DPAPI-machine-key-encrypted local queue with a cap, a time-to-live, and backoff, so a JIM outage does not lose changes and a long outage does not fill the Domain Controller's disk.
- **Deployment and coverage monitoring.** The agent must be installed on every writable Domain Controller or changes processed elsewhere are silently missed; registration requires a reboot. JIM should therefore report agent check-ins and alert on a Domain Controller that stops reporting.
- **A real Active Directory lab.** Multi-Domain-Controller, across supported Windows Server versions. This cannot be validated in the development container or the cloud sandbox.
- **Independent security assessment.** A component that sees every cleartext password in the domain will be threat-modelled and penetration-tested by customers; commissioning that ourselves first is cheaper than discovering it in a customer's review.
- **Licensing and redistribution terms** for a distributed binary agent, distinct from the server licence.

Two mitigations worth weighing against that cost. First, the agent is only needed where the directory is the password master; customers whose password master is the identity provider, or who adopt a future self-service reset feature, need only Phase 1 and Phase 2. Second, the Windows notification-package registration accepts multiple filters, so JIM's agent can be installed alongside an incumbent product's during a parallel-run migration, which materially de-risks a migration cutover. Note that an incumbent capture agent cannot simply be repointed at JIM: those agents speak a proprietary protocol to their own synchronisation service, so a migration necessarily involves deploying ours.

### Rejected and deferred inbound channels

**Traditional ILM systems have no file-based password channel, and neither should Phase 1.** Their inbound password path is exclusively capture-at-change push from a Domain Controller agent, plus a programmatic interface on the synchronisation engine for administrative set and reset. Passwords are not part of import runs, and there is no password column in any import. The industry pattern is consistent across the major products: a directory password filter pushing to the synchronisation engine. Nothing in that landscape requires JIM to accept passwords in a file for migration parity, so a CSV channel is not a migration blocker.

It is worth noting how closely the incumbent model matches what this PRD proposes, which is reassuring for migrating operators: password synchronisation is configured per Management Agent rather than per rule, there is an explicit enable toggle, targets implement a password interface distinct from the export interface, per-target retry count and interval are configurable, secure transport can be mandated, and password synchronisation history is retained and queryable. JIM's differentiators are that the queue and history are first-class objects in the portal with manual retry, rather than being reachable only through a management interface.

A file-based bulk initial-password load has genuine uses (bulk onboarding, one-way transfer into a high-side network, migration cutover) but is deferred rather than designed in, because cleartext passwords sitting in a file are a serious exposure: readable by backup agents, replicated by file sync, and impossible to attribute. If it is ever built, the minimum controls are envelope encryption to a JIM-held public key so a file at rest is useless on its own, consume-and-shred semantics, forced change-at-next-sign-in, per-file audit, and an explicit opt-in setting. The better answer to "an inbound channel that is not our own API" is a SCIM inbound path, where `password` is a standard write-only attribute, which fits the existing post-MVP SCIM plan and is a standard rather than a bespoke file format.

### Design note: why passwords do not use the export pipeline

The export pipeline persists per-attribute change values, surfaces them in previews and change history, retries from that persisted state, and expects confirmation by re-import. Every one of those behaviours is wrong for a secret that must be write-only, unreadable, and unconfirmable. Reusing Pending Exports would put cleartext passwords into export previews and change records, and would leave the pipeline waiting forever for a confirmation that can never arrive. The parallel channel is more work than reusing the existing rails and is the only correct option.
