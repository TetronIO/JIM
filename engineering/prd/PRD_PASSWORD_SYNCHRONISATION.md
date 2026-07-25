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
- **File-based (CSV) password ingest.** Deliberately deferred; the supported way to bring a password in from a source is Phase 2 inbound password mapping on import, which never lands the value in the Metaverse. See "Rejected and deferred inbound channels".
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
13. A password change that targets zero enabled systems is still recorded as an Activity, with an outcome that makes the no-op explicit.

**Preventing cleartext passwords leaking into the Metaverse**

14. Well-known credential attributes are denylisted in connector schema handling: they cannot be selected for import, cannot be targeted by an Attribute Flow, and are reachable only through the password channel. The initial list for LDAP is `unicodePwd`, `userPassword`, `dBCSPwd`, `ntPwdHistory`, `lmPwdHistory`, `supplementalCredentials`, `unixUserPassword`, and `msDS-ManagedPassword`. This is distinct from, and additional to, the existing protected-attribute defaults list, which concerns clearing attributes rather than credentials.
15. Where an Attribute Flow targets an attribute whose name matches a credential-like pattern outside that list, configuration validation raises a warning explaining that the password channel exists and that flowing a password as an attribute persists it in Metaverse Object attribute values, change history, Pending Exports, export previews, search results, and database backups.

**Connector capability**

16. A new capability interface, provisionally `IConnectorPasswordManagement`, exposes a set-password operation against a Connected System Object, plus open and close semantics consistent with the existing export capability interfaces.
17. A corresponding capability flag is added to the connector capability contract and mirrored on the persisted Connector Definition.
18. The LDAP connector implements the capability: Active Directory mode writes `unicodePwd` using the required quoted UTF-16LE encoding and **must refuse to transmit unless the connection is LDAPS**; generic LDAP mode writes `userPassword`. The mode is selected by a per-system setting.
19. The connector supports an initial-password-on-create flow and an optional "user must change password at next sign-in" behaviour where the target system has such a concept.

**Reporting and audit**

20. A Password Synchronisation queue page lists queued, failed, and expired events with target Connected System, target object, status, error detail, attempt count, and next retry time. It never displays, and its backing DTO never carries, the password value.
21. The queue page supports manual retry of a single event and of a filtered selection, and supports cancelling or deleting an event.
22. Every password change event produces an Activity: a parent Activity for the change and a child outcome per target Connected System recording success or failure.
23. A new Activity target type is added for Password Synchronisation, mapped to a **new Activity target category** so the existing Activities list quick-filter isolates password events with no new filter controls. Per-Connected-System filtering comes for free from the existing filter options provided the Activity sets its target context to the Connected System name.
24. The Metaverse Object detail page gains a Password Synchronisation panel showing that identity's recent password events and their per-system outcomes. The panel is visible to administrators only.
25. The Connected System list view indicates Password Synchronisation state per row, and supports filtering and sorting on it.
26. Queue rows and their Activities are reconciled: a queue row links to the Activity that created it, and the Activity records the terminal outcome. The queue holds operational state only; the Activity is the durable audit record and outlives the queue row.

**Retention**

27. The Pending Password Change queue is trimmed automatically by a Schedule, not by worker housekeeping, consistent with the direction of #1118. Terminal-state rows older than a retention period are removed; live rows are never trimmed.
28. Password event Activities are retained under their own retention class and Service Setting, alongside the existing general, configuration-change, and security-event retention periods.
29. Trim operations are batched using the existing cleanup batch-size setting and report summary statistics.

**Surfaces**

30. Setting a password is available on all three surfaces: a portal dialog, a REST endpoint, and a PowerShell cmdlet.
31. Password Synchronisation configuration (create, read, update, enable, disable) is available on all three surfaces.
32. Queue read and manual retry are available on all three surfaces.
33. REST endpoints that accept a password are administrator-only, are excluded from request logging, and reject the request unless the transport is secure.

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
| Connectors | New password capability implemented by the LDAP connector (AD `unicodePwd` and generic `userPassword` modes, LDAPS enforcement); credential-attribute denylist in schema handling; Connector Factory wiring; Mock connector support for testing |
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

## Resolved Decisions

These were open during drafting and have since been decided; they are settled inputs to the implementation plan, not still-open questions.

1. **Fan-out scope: all enabled systems, no scoping filter.** A password change fans out to every Connected System that has Password Synchronisation enabled and in which the identity has a Connected System Object. There is deliberately no per-system scoping expression in v1; keep it simple. (Scoping can be added later if a real need appears.)
2. **Unprovisioned target: queue it.** A password change for an identity that has no Connected System Object in an enabled target yet is queued rather than failed, bounded by the event time-to-live, so the provisioning-then-password race resolves itself when the account appears.
3. **Initial-password generation: expression engine for v1, with a first-class generator to follow.** v1 reuses the existing expression engine. This is explicitly an interim answer. The longer-term aim is a built-in password-generation mapping function with real-world options (length, character-class rules, pronounceable or passphrase styles, per-target policy alignment, exclusion of ambiguous characters, and so on), designed from a blank slate around the configuration expectations and pain points administrators hit with traditional ILM systems, so defining a sensible default password is a first-class, low-friction action rather than a hand-written expression. Out of scope for this PRD; worth its own design once v1 ships. The v1 model should not paint that in: keep initial-password sourcing behind a seam that a later generator can plug into without reworking the queue or delivery path.
4. **Default event time-to-live: 7 days, configurable per Connected System.** Long enough to ride out a realistic outage, short enough not to resurrect a stale password indefinitely.

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
- [ ] Well-known credential attributes cannot be selected for import or targeted by an Attribute Flow, and a credential-like attribute name outside the denylist raises a configuration warning.
- [ ] No password value appears in any log, Activity, change record, preview, DTO, or API response; verified by test and by security review.
- [ ] Unit tests cover fan-out, coalescing, expiry, retry, enable-drain, and the never-log invariant; integration tests cover end-to-end delivery to a directory.
- [ ] Public documentation ships in the same pull request.

## Additional Context

### Phasing

**Phase 1: JIM as password origin.** Everything in this document: configuration, queue, connector capability, delivery, reporting, audit, retention, and the three surfaces. Password changes originate from an administrator in the portal, from the REST API, from PowerShell, or from provisioning.

**Phase 2: inbound capture.** Two channels sharing one entry point into the password channel:

- *Ingress API.* A documented, API-key-authenticated endpoint that an external capture agent posts password change events to, with payload envelope encryption so that a TLS-terminating proxy cannot recover the password, a versioned wire contract, replay protection, and per-agent check-in reporting so an administrator can see which capture agents are healthy.
- *Inbound password mapping on import.* A per-Connected-System setting nominating a source attribute as a password. The value is diverted at the import boundary straight into the password channel and is **never** persisted as a Connected System Object or Metaverse Object attribute value, never written to change history, and never available to an Attribute Flow. This is the supported answer to "my authoritative source supplies initial passwords", it works with any connector including the File connector, and it is strictly safer than the file-ingest idea considered below because the value never lands in the Metaverse. It is also the supported alternative to the do-it-yourself route described under "Why credential attributes are denylisted".

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

- **Signing under LSA Protection is the go/no-go item.** A Windows password filter is a native DLL loaded into LSASS. Where a customer has enabled LSA protection (RunAsPPL), LSASS will only load plugins meeting the protected-process signing requirements, which for a third party means a Microsoft-issued signature obtained through the Microsoft hardware developer signing programme rather than an ordinary code-signing certificate. This has a long lead time and needs confirming with Microsoft before any engineering commitment, because JIM's target sectors are exactly the ones that enable LSA protection.
- **Code-signing certificate.** Organisation-validated or extended-validation certificate for Tetron Limited, with the private key on FIPS-compliant hardware or a cloud signing service, plus timestamping so signatures outlive certificate expiry. Required for the DLL, the service binary, and the installer.
- **Split-process architecture, non-negotiable.** The filter DLL is called synchronously by LSASS on every password change; a fault kills LSASS and takes the Domain Controller down, and latency there slows every password change in the domain. The DLL must therefore do almost nothing: hand the change to a local Windows service over local IPC, and let that service own durable queueing, encryption, retry, and network delivery.
- **Native toolchain and CI, with Rust preferred for the LSASS-resident DLL.** The DLL must be native with no managed runtime (the CLR cannot be hosted inside LSASS), which historically meant C or C++. Rust is the better choice *precisely here*, and for a reason specific to this component rather than general preference: a memory-safety fault in a DLL loaded into LSASS crashes LSASS and takes the Domain Controller down, so the buffer-overflow and use-after-free class that has produced real CVEs in C password filters is not a code-quality nuisance here, it is a domain-wide outage. Rust removes that class at compile time while still emitting a plain native `cdylib` exporting the required C-ABI entry points (`InitializeChangeNotify`, `PasswordFilter`, `PasswordChangeNotify`), with mature Microsoft-maintained Windows bindings (the `windows` crate). Caveats that must be honoured: compile with `panic = "abort"` (or wrap every FFI entry point in `catch_unwind`) so a Rust panic can never unwind across the FFI boundary into LSASS; keep `unsafe` confined to the thin Win32 interop shim; and note that Rust changes nothing about the signing requirement above (the LSA Protection signature is language-agnostic). The one genuine cost is team familiarity, but a password filter is a small, sharply-bounded component and therefore a sound first Rust footprint rather than a risky one. The userspace service is a separate decision: it can also be Rust for a single toolchain, or .NET to match the rest of JIM, since it does not run inside LSASS and is not subject to the same constraint. Alongside the toolchain: Windows build agents, a signed MSI built with a proper installer toolchain, upgrade codes, and a clean uninstall path.
- **Local durability.** A DPAPI-machine-key-encrypted local queue with a cap, a time-to-live, and backoff, so a JIM outage does not lose changes and a long outage does not fill the Domain Controller's disk.
- **Deployment and coverage monitoring.** The agent must be installed on every writable Domain Controller or changes processed elsewhere are silently missed; registration requires a reboot. JIM should therefore report agent check-ins and alert on a Domain Controller that stops reporting.
- **A real Active Directory lab.** Multi-Domain-Controller, across supported Windows Server versions. This cannot be validated in the development container or the cloud sandbox.
- **Independent security assessment.** A component that sees every cleartext password in the domain will be threat-modelled and penetration-tested by customers; commissioning that ourselves first is cheaper than discovering it in a customer's review.
- **Licensing and redistribution terms** for a distributed binary agent, distinct from the server licence.

Two mitigations worth weighing against that cost. First, the agent is only needed where the directory is the password master; customers whose password master is the identity provider, or who adopt a future self-service reset feature, need only Phase 1 and Phase 2. Second, the Windows notification-package registration accepts multiple filters, so JIM's agent can be installed alongside an incumbent product's during a parallel-run migration, which materially de-risks a migration cutover. Note that an incumbent capture agent cannot simply be repointed at JIM: those agents speak a proprietary protocol to their own synchronisation service, so a migration necessarily involves deploying ours.

### Why credential attributes are denylisted

Nothing in JIM today stops an administrator importing a cleartext password into a plain text attribute and mapping it, via an Attribute Flow, to export as `unicodePwd` or `userPassword`. The LDAP connector already carries `byte[]` attribute values end to end, and the only existing attribute guard (the protected-attribute defaults list) is about *clearing* attributes in AD, not credentials. So the do-it-yourself route works, and some administrators will reach for it the moment they see JIM can export to a directory. We should treat that as a hazard to close off, not a feature to rely on, for three reasons:

- **It persists the secret in the clear throughout the Metaverse.** A password mapped as an attribute is stored as a Connected System Object attribute value and a Metaverse Object attribute value, written into CSO and MVO change history, materialised as a Pending Export, shown in export previews, returned by search and the API, and captured in every database backup. It is exactly the exposure this whole feature exists to avoid, reintroduced by the back door.
- **The `unicodePwd` write will usually fail anyway.** AD only accepts `unicodePwd` as a quoted UTF-16LE value over LDAPS with modify semantics that the generic export path does not produce, so the DIY mapping tends to *look* configured while silently never setting a password, which is worse than an honest refusal.
- **It has no queue, no retry, no audit-without-the-value, and no coalescing.** It bypasses everything Phase 1 provides.

Hence requirements 14 and 15: well-known credential attributes are denylisted from import and from Attribute Flow selection, and a credential-like attribute name outside that list raises a configuration warning pointing the administrator at the password channel (or, in Phase 2, at inbound password mapping on import, which is the supported way to bring a password *in* from a source without it ever touching the Metaverse). The denylist is not a security boundary against a determined administrator (they own the schema and could rename an attribute), it is a guardrail that makes the safe path the obvious one and the dangerous path deliberate.

### Rejected and deferred inbound channels

**Traditional ILM systems have no file-based password channel, and neither should Phase 1.** Their inbound password path is exclusively capture-at-change push from a Domain Controller agent, plus a programmatic interface on the synchronisation engine for administrative set and reset. Passwords are not part of import runs, and there is no password column in any import. The industry pattern is consistent across the major products: a directory password filter pushing to the synchronisation engine. Nothing in that landscape requires JIM to accept passwords in a file for migration parity, so a CSV channel is not a migration blocker.

It is worth noting how closely the incumbent model matches what this PRD proposes, which is reassuring for migrating operators: password synchronisation is configured per Management Agent rather than per rule, there is an explicit enable toggle, targets implement a password interface distinct from the export interface, per-target retry count and interval are configurable, secure transport can be mandated, and password synchronisation history is retained and queryable. JIM's differentiators are that the queue and history are first-class objects in the portal with manual retry, rather than being reachable only through a management interface.

A file-based bulk initial-password load has genuine uses (bulk onboarding, one-way transfer into a high-side network, migration cutover) but is deferred rather than designed in, because cleartext passwords sitting in a file are a serious exposure: readable by backup agents, replicated by file sync, and impossible to attribute. If it is ever built, the minimum controls are envelope encryption to a JIM-held public key so a file at rest is useless on its own, consume-and-shred semantics, forced change-at-next-sign-in, per-file audit, and an explicit opt-in setting. The better answer to "an inbound channel that is not our own API" is a SCIM inbound path, where `password` is a standard write-only attribute, which fits the existing post-MVP SCIM plan and is a standard rather than a bespoke file format.

### Design note: why passwords do not use the export pipeline

The export pipeline persists per-attribute change values, surfaces them in previews and change history, retries from that persisted state, and expects confirmation by re-import. Every one of those behaviours is wrong for a secret that must be write-only, unreadable, and unconfirmable. Reusing Pending Exports would put cleartext passwords into export previews and change records, and would leave the pipeline waiting forever for a confirmation that can never arrive. The parallel channel is more work than reusing the existing rails and is the only correct option.
