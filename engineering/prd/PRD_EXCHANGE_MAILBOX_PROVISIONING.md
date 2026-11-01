# Exchange Mailbox Provisioning (on-premises)

- **Status:** Planned
- **Created:** 2026-10-31
- **Author:** Jay (drafted by Claude for review)
- **Issue:** [#2021](https://github.com/TetronIO/JIM/issues/2021)
- **Transport spike:** [`engineering/plans/doing/EXCHANGE_REMOTING_SPIKE.md`](../plans/doing/EXCHANGE_REMOTING_SPIKE.md). Phases 0, 1a and 2 are complete; the go/no-go waits on Phases 1b and 3 against the AD lab's Exchange server.

## Problem Statement

Organisations that provision Active Directory accounts with JIM also need most of those accounts to be Exchange recipients: a mailbox for a joiner, a shared mailbox for a team, a mail contact for a colleague in another forest. Since Exchange 2007 there is no Recipient Update Service, so stamping `msExch*` attributes does not create a mailbox. An Exchange cmdlet (`Enable-Mailbox` and its siblings) has to run, and on-premises Exchange only exposes those cmdlets through Exchange Remote PowerShell.

Traditional ILM solutions leave administrators to hand-craft Exchange's attributes with Attribute Flows, then run a post-export extension that calls Exchange. That is fiddly in three ways:

- Administrators must know which attributes each Exchange version needs and how to format them (`homeMDB` as a mailbox database's distinguished name, for example).
- The identity system, not Exchange, chooses the mailbox database, so administrators write database-balancing logic in expressions.
- When the Exchange step fails after the directory create succeeds, the account is left half-provisioned, and nothing says so.

JIM has none of this today. The LDAP Connector writes Exchange attributes like any other, which creates nothing, and there is no step that runs after an export.

## Goals

- An administrator makes JIM-provisioned directory objects Exchange recipients by declaring each object's recipient type, without knowing Exchange's attributes or cmdlets.
- Every common recipient is covered: user, shared, room and equipment mailboxes, mail users, mail-enabled groups, and mail contacts.
- Address changes after creation reach Exchange. A change of recipient type is reported rather than made, until leaver handling follows.
- Exchange chooses the mailbox database by default, honouring the controls Exchange administrators already use; rule-based placement is possible.
- Cross-forest GAL synchronisation works as an ordinary JIM pattern, including replies to old messages.
- A failed Exchange step is visible on the Activity, retried, and never leaves JIM believing a recipient exists.
- Everything is configurable from the portal, the REST API and PowerShell.
- Nothing beyond Exchange itself is needed: no Windows host, no internet access, no extra service.

## Non-Goals

- Exchange Online and hybrid remote mailboxes. A separate issue follows.
- Leaver handling in Exchange: converting a leaver's mailbox to shared, hiding them from the address book, or disabling the recipient, and converting between mailbox types generally. It follows later, paired with JIM's leaver actions (Delete, Disconnect, and Disable once that exists). In the first release, JIM's existing deprovisioning applies: see requirement 18.
- Mailbox permissions (Full Access, Send As, a manager's access to a leaver's mailbox), archive mailboxes, linked mailboxes, and mailbox policies (retention, address book, quotas). These come later.
- Guided setup templates beyond the Exchange recipients panel (requirement 4). Templates that generate several flows at once, such as a mailbox provisioning pattern or GAL sync, follow in their own PRD.
- JIM-side database balancing ("least-populated database"), unless customers ask for it.
- Running script text. JIM only ever calls named cmdlets with bound parameters.
- Managing Exchange's own configuration: databases, Email Address Policies, connectors.
- Exchange 2013 and earlier.

## User Stories

1. As an identity administrator, I want a joiner provisioned into Active Directory to get a mailbox automatically, so that they can work on day one without a second ticket.
2. As an Exchange administrator, I want new mailboxes placed by Exchange's own automatic distribution, or by a rule I define, so that databases stay balanced and I keep control of placement.
3. As a messaging administrator in a multi-forest organisation, I want each forest's people to appear as mail contacts in the other forests' address books, so that everyone can find and reply to everyone.
4. As an operator, I want every failed Exchange step to show on the Activity with a clear reason and to be retried, so that no account is silently left half-provisioned.
5. As an automation engineer, I want to configure all of this with PowerShell and the REST API, so that environments are reproducible.
6. As a security officer, I want JIM to manage Exchange with its existing least-privilege account, over an encrypted, Kerberos-authenticated connection, so that recipient management meets our security baseline.

## Requirements

### Functional Requirements

**Configuration**

1. An LDAP Connected System connected to Active Directory can manage Exchange recipients. Its Exchange settings are LDAP Connector settings, under an Exchange heading, so only LDAP Connected Systems show them, and the REST API and PowerShell handle them as they handle any Connector setting:
   - **Manage Exchange recipients**, a checkbox that reveals the rest. Saving it turned on is refused when the Connected System's detected directory type is not Active Directory.
   - **Exchange server.** JIM offers the Exchange servers recorded in Active Directory's configuration partition, and accepts an endpoint instead (default `http://<server>/PowerShell/`), such as a load-balanced name or an `https://` address. One endpoint per Connected System. Reading the servers from the configuration partition is to be confirmed against the AD lab in spike Phase 1b.
   - **CA certificate**, shown only for an `https://` endpoint.
2. JIM signs in to Exchange with the Connected System's own credentials; there is no second account. It works out the Kerberos details itself: the principal from the credential's account (given as `DOMAIN\name`, a user principal name or a distinguished name, and resolved through the directory), the realm from the DNS name of that account's domain, and the KDC as the domain controller the Connected System is pinned to, since every domain controller is one. An operator never enters a realm or a KDC, or mounts a `krb5.conf`. Both of the LDAP Connector's authentication types, Simple and NTLM, carry the password Kerberos needs.
3. When the Exchange settings are saved, alongside the directory connection check that already runs then, JIM opens an Exchange session. A save that cannot reach Exchange, or whose sign-in Exchange refuses, is refused with the reason. A cmdlet JIM needs that the account's Exchange roles do not allow (Exchange's session lists only the cmdlets an account may run) is reported by name, with the recipient types it affects, as a warning that does not refuse the save. The Exchange version and the Kerberos sign-in JIM worked out are shown among the Connected System's detected facts.

**Declaring recipient state**

4. The LDAP Connector offers connector-defined attributes that Synchronisation Rules flow like any other attribute:

   | Attribute | Meaning |
   |---|---|
   | Exchange Recipient Type | None, Mailbox, SharedMailbox, RoomMailbox, EquipmentMailbox, MailUser, MailContact or MailEnabledGroup |
   | Exchange Alias | The recipient's alias; falls back to `mailNickname` |
   | Exchange Database | Mailbox types only. When empty, Exchange chooses. |
   | Exchange Primary SMTP Address | Optional. When empty, Email Address Policies apply. |
   | Exchange External Address | MailUser and MailContact: the external SMTP address |

   On the Attribute Flow tab of an export Synchronisation Rule whose Connected System manages Exchange, an **Exchange recipients** panel lists these attributes, offering only those that apply to the rule's Object Type, each with the Attribute Flow that sets it or what happens without one ("Database: not set, so Exchange chooses"). With none set, the panel is the way in to setting up recipients. Set, Edit and Remove open the standard Edit Attribute Flow dialog with the attribute already chosen, and **Stop provisioning recipients** removes those flows after a confirmation that says existing recipients are left as they are (requirement 16). The panel stores nothing of its own and finds flows by their target alone, never interpreting an expression. It is a portal affordance only: everything it does is an Attribute Flow operation the REST API and PowerShell already offer.

5. A recipient type that does not fit the object is refused with a clear error rather than attempted: a mailbox on a group, MailContact on anything but a `contact`, or MailEnabledGroup on a group that is not universal.
6. The `contact` object class can be selected as an Object Type on an LDAP Connected System, so contacts can be provisioned.

**The Exchange step**

7. After a successful directory create or update, when an object's declared recipient state differs from its last imported state, JIM queues a durable Exchange change for it, as the export queues an initial password today. The queued change holds the declared state and the domain controller written to, and no secret.
8. The step names the domain controller the directory write went to (`-DomainController`). Exchange runs on its own servers and otherwise picks a domain controller for itself, which may not have the new object yet: replication takes seconds within a site and can take 15 minutes or more between sites, and Exchange then reports "object not found".
   - JIM records the domain controller it actually wrote to, from the bound connection's RootDSE `dnsHostName`, because the Connected System's host may be a domain name that any domain controller answers.
   - When Exchange cannot reach that domain controller, the step fails with an error naming both servers. It does not fall back to Exchange's own choice, which would bring back the replication failure.
9. The step is idempotent: it reads the recipient's current state first and does only what converges it. Re-running against a recipient already in its declared state changes nothing.
10. A background delivery lane, like the [Password Delivery Service](../../docs/concepts/password-synchronisation.md#the-password-delivery-service), delivers each queued change within seconds of the export writing it, without waiting for another export run. It claims each change with a lease, so a Worker that stops mid-delivery cannot strand it, and it serves the whole queue over one Exchange session.
11. Each queued change ends in one of these states, shown on the Activity and against the object:
    - **Delivered:** Exchange has the declared state. The queued change is removed; the Activity is the history.
    - **Retrying:** Exchange was unreachable, timed out, or could not see the object yet. JIM retries with backoff, from 2 minutes between attempts doubling to at most an hour. These values are fixed in the first release, with no setting.
    - **Parked:** Exchange refused, or 24 hours of retries failed. Nothing changes until a person retries it or the declared state changes. Parking is not expiry: the change is kept, because unlike a queued password, a declared recipient does not go stale.
    - **Superseded:** a newer declared state for the same object replaces it, so only the latest is ever delivered.
    - **Withdrawn:** the object, or the Synchronisation Rule that declared it, was removed first. This is not a failure.
    - **Cancelled:** an administrator decided it should not be delivered. Like Parked, this holds until the object's declared state changes: later exports do not queue the same change again.
12. A failed change never rolls back or hides the directory write, and JIM never treats the recipient as existing until an import shows it.
13. Each queued change's Activity has the export's Run Profile Execution Item for the object as its parent (#2668), so an administrator goes straight up from "Mailbox for Alice" to Alice's export, and that item's Follow-up section shows the mailbox with its current outcome. Delivery attempts are children of the change's Activity. These Activities never appear at the top level of the Activities list, and the run's own status is final: a follow-up that fails later never changes it. In the portal, as the [mock](https://claude.ai/artifact/WJDkMng3Ji2G7iPB4BG6cR) shows:
    - The run's row in the Activities list, and the run's own page, sum its follow-ups by outcome.
    - The Causality Explorer's Lineage shows each follow-up in the account's own column under "After this run", with its live outcome and attempts, and says while any follow-up is still running.
    - The object's timeline ("What happened to it") gives each follow-up one row that updates in place; its attempts are on its Activity.
    - Operations has an Exchange tab beside Passwords, listing every queued change not yet delivered, across all Connected Systems, with retry and cancel. Service Health covers both delivery lanes.
    - The Connected System Object shows its declared recipient state beside what the last import read back from Exchange, with any outstanding change.
14. Shared, room and equipment mailboxes are created on disabled directory accounts, and JIM writes the account disabled.

**Lifecycle**

15. A change from None to a recipient type enables the recipient (`Enable-Mailbox`, `-Shared`, `-Room` or `-Equipment`, `Enable-MailUser`, `Enable-MailContact`, `Enable-DistributionGroup`).
16. A change of recipient type on an existing recipient, including to None, is not made in the first release. It is reported on the Activity against the object, and nothing changes.
17. Alias, primary SMTP address and external address changes reach Exchange (`Set-Mailbox`, `Set-MailUser`, `Set-MailContact`, `Set-DistributionGroup`).
18. When JIM deletes a recipient's directory object (the Delete deprovisioning action), no Exchange step runs. Exchange disconnects a deleted user's mailbox itself and keeps it for the mailbox database's deleted-mailbox retention period (30 days by default), during which an Exchange administrator can reconnect it. Other recipients are directory objects only, so they go with the object. When JIM disconnects an object instead, its recipient is left as it is.

**Database placement**

19. With Exchange Database empty, Exchange's automatic mailbox distribution chooses, honouring `IsExcludedFromProvisioning` and `IsSuspendedFromProvisioning`.
20. With Exchange Database set, typically by an expression, that database is used. A database that does not exist fails the step with a clear error.

**GAL synchronisation**

21. A Synchronisation Rule can provision in-scope Metaverse Objects from other forests as `contact` objects in a designated OU of each target forest, as MailContact recipients whose external address is the source recipient's primary SMTP address.
22. Each contact's `proxyAddresses` include an `X500:` address made from the source recipient's `legacyExchangeDN`, so replies to old messages and cached entries do not bounce.
23. Contacts JIM created are never imported back and projected as people. The documented setup leaves the GAL OU out of the importing Connected System's scope, and JIM warns when an importing Connected System's scope includes a container that a Synchronisation Rule provisions contacts into. The warning shows in the portal and comes back from the REST API and PowerShell when scope is set there.
24. Address book attributes (display name, company, department, phone numbers) flow as ordinary attributes, and a contact is deleted when its source leaves scope, under the normal deprovisioning rules.

**Import and reporting**

25. The next import picks up the values Exchange stamped (`mail`, `proxyAddresses`, `legacyExchangeDN`, `homeMDB`, `msExchMailboxGuid`, `msExchRecipientTypeDetails`), so JIM's view converges with Exchange's.
26. Each failure is classified so an administrator can tell them apart: object not found, refused by Exchange (with Exchange's own message), insufficient permissions, authentication failure, and unreachable or timed out. The classification decides whether the change is Retrying or Parked.
27. Each delivery pass logs summary statistics: changes delivered, retrying, parked and superseded, plus the session's Kerberos logins and connections.

**Surface parity**

28. Exchange settings, the connector-defined attributes, and the queued Exchange changes with their outcomes (including retry and cancel) are available in the portal, the REST API and PowerShell, with tests and docs on each.

### Non-Functional Requirements

- **Transport security.** Kerberos only. By default, HTTP with Kerberos message encryption, as Microsoft documents for Exchange Remote PowerShell; the transport refuses any unencrypted session and never falls back to one. HTTPS is optional, with TLS 1.2 or later and certificate validation always on. This is a recorded exception to TLS in transit (`engineering/COMPLIANCE_MAPPING.md`, SC-8).
- **Secrets.** Exchange is reached with the Connected System's own credentials, which are already encrypted at rest (AES-256-GCM) and never logged; the remoting library cannot give the password back once set. The cost of one account is that it also holds an Exchange role: the docs recommend a least-privilege custom RBAC role covering only the cmdlets above, with Recipient Management as the simpler alternative.
- **Throughput.** One session serves a whole export batch, with one Kerberos login (the spike measured 200 commands on 1 login and 3 connections). Parallel sessions per Connected System are bounded well below Exchange's default `PowerShellMaxConcurrency` of 18. Per-step cost against real Exchange is measured in spike Phase 3 and recorded there.
- **Runs where the Worker runs.** A Linux container with a read-only root filesystem, as a non-root user, air-gapped. The spike proved this in the Worker image; it needs `XDG_CACHE_HOME=/tmp/.cache`.
- **Exchange versions.** Exchange Server SE is lab-tested, 2019 CU15 is supported but not lab-tested, and 2016 is best effort.

## Examples and Scenarios

### Scenario 1: A joiner gets a mailbox

**Given**: an export Synchronisation Rule flows `"Mailbox"` to Exchange Recipient Type for people, and leaves Exchange Database empty
**When**: an export run provisions Alice's account into PANOPLY
**Then**: Alice's account is created, an Exchange step runs `Enable-Mailbox` on the same domain controller, Exchange places her mailbox in a database it chooses, and the next import brings back her `mail`, `proxyAddresses` and `homeMDB`

### Scenario 2: Placement by rule

**Given**: Exchange Database is flowed with `mv["Department"] == "Executive" ? "MDB-VIP" : null`
**When**: an executive and an engineer join
**Then**: the executive's mailbox is in `MDB-VIP`, and the engineer's is wherever Exchange's distribution puts it

### Scenario 3: A leaver's account is deleted

**Given**: people who leave are deprovisioned with the Delete action
**When**: Bob leaves
**Then**: JIM deletes Bob's account and runs no Exchange step. Exchange disconnects his mailbox and keeps it for 30 days, during which an Exchange administrator can reconnect it to an account.

### Scenario 4: GAL sync between two forests

**Given**: Synchronisation Rules provision PANOPLY's people as contacts in `OU=GALSync` in RESURGAM, and the reverse, with source rules scoped to exclude each GAL OU
**When**: Carol joins in PANOPLY with a mailbox
**Then**: a contact for Carol appears in RESURGAM's address book with her PANOPLY address and an `X500:` proxy of her `legacyExchangeDN`, and RESURGAM's next import does not project her contact as a second person

### Scenario 5: Exchange is unreachable

**Given**: Dave's account was created, and the Exchange server is down
**When**: the Exchange step runs
**Then**: Dave's account stays created, and his queued Exchange change shows as Retrying against him, with Exchange's reason. When Exchange is back, the delivery lane's next attempt creates his mailbox, without waiting for another export run.

### Scenario 6: A group that cannot be mail-enabled

**Given**: Exchange Recipient Type is flowed as `MailEnabledGroup` for a global security group
**When**: the export runs
**Then**: no Exchange step is attempted, and the Activity reports that only universal groups can be mail-enabled

### Scenario 7: Nothing to do

**Given**: Erin's mailbox already matches her declared state
**When**: a Full Synchronisation and export run again
**Then**: no Exchange step is staged, and Exchange is not contacted for her

### Scenario 8: A recipient type change waits for leaver handling

**Given**: Frank has a mailbox
**When**: his declared Exchange Recipient Type changes to SharedMailbox
**Then**: nothing changes in Exchange, and the Activity reports, against Frank, that changing a recipient's type is not supported in this release

## Constraints

- JIM is pre-release, so no backwards compatibility is needed, but the database is migrated additively, as always.
- The transport lives in the standalone remoting library (`src/Tetron.PowerShellRemoting/`), which may not reference `JIM.*`; a test enforces it. The Exchange cmdlet calls live in the LDAP Connector.
- No new third-party packages beyond `System.Management.Automation` 7.6.x, already approved for the spike.
- No third-party product names in code, docs or UI.
- British English throughout.

## Affected Areas

| Area | Impact |
|------|--------|
| Remoting library | Already built by the spike; moves from spike to production use |
| LDAP Connector | Exchange settings, connector-defined attributes, the Exchange step, the `contact` Object Type |
| Models and database | A durable queue of Exchange changes (like `PendingPasswordChange`) and the failure classifications; a migration. The Exchange settings are Connector settings and need no model of their own |
| Worker | Queueing after directory writes, and a delivery lane beside the Password Delivery Service's |
| Application | Export execution (`ExportExecutionServer`), and settings validation, which also opens an Exchange session |
| API and PowerShell | Queued Exchange changes, with retry and cancel. The settings and the recipients panel need nothing new: they are Connector settings and Attribute Flows |
| Portal | The Exchange settings among the LDAP Connector's settings and detected facts; the Exchange recipients panel on export Synchronisation Rules; queued Exchange changes and their outcomes on Activities, in Operations and on the object, with retry and cancel |
| Worker image | `XDG_CACHE_HOME` and `POWERSHELL_TELEMETRY_OPTOUT` |
| Integration tests | New scenarios against the AD lab's Exchange servers, including GAL sync both ways |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/connectors/jim-ldap-connector-exchange.md` (new) | How to set up Exchange provisioning: settings, recipient types, placement, lifecycle, troubleshooting |
| `docs/connectors/jim-ldap-connector-gal-sync.md` (new) | GAL sync between forests, including the X500 proxy and loop prevention |
| `docs/connectors/jim-ldap-connector-permissions.md` | The least-privilege Exchange RBAC role the Connected System's account needs |
| `docs/configuration/activity-execution-items.md` | How queued Exchange changes and their outcomes appear on Activities |
| `engineering/COMPLIANCE_MAPPING.md` | SC-8 moves from planned to in place |
| `engineering/DEVELOPER_GUIDE.md` | The remoting library and its boundary |

## Dependencies

- The spike's go/no-go: Phases 1b and 3 against the AD lab ([plan](../plans/doing/EXCHANGE_REMOTING_SPIKE.md)).
- Follow-up Activities parented by their execution item (#2668), done first for initial passwords and reused here.
- The AD lab's Exchange servers (#2412): `exchange-primary` in PANOPLY exists; `exchange-source` in RESURGAM is needed for GAL sync in both directions.

## Decisions

Made on 2026-10-31, in review of this draft:

1. **Attribute Flows are always how the configuration is persisted.** Guided experiences sit on top and configure those flows for the administrator; nothing is stored outside them.
2. **Leaver handling in Exchange is not in the first release.** Converting to shared, hiding and disabling follow later, paired with JIM's leaver actions (Delete, Disconnect, and Disable once that exists). The first release keeps to creation and address changes so it can ship for v1.0.0.
3. **One Exchange endpoint per Connected System.** Failover lists and load-balanced namespaces follow later.
4. **Parallel sessions per Connected System are set by measurement**, from spike Phase 3 and the integration tests, not chosen in advance.
5. **Exchange's serialisation payload signing needs nothing here.** Spike Phase 3 confirms it does not affect the client.
6. **Exchange settings are LDAP Connector settings, not a tab of their own**, so only LDAP Connected Systems show them, and enabling them is refused unless the directory is Active Directory (requirement 1).
7. **Exchange is managed with the Connected System's own account.** There is no second account in the first release: it would isolate little, since the account already writes the objects Exchange acts on, and an optional override can follow later without breaking anything. JIM works out the Kerberos realm and KDC from the domain (requirement 2).
8. **The first release has one guided layer, the Exchange recipients panel** (requirement 4), over ordinary Attribute Flows. Templates follow in their own PRD.

Made on 2026-11-01, in review of the [portal mock](https://claude.ai/artifact/WJDkMng3Ji2G7iPB4BG6cR):

9. **Follow-ups sit in the account's own Lineage column**, under "After this run". A separate Exchange column would read as a second object when the recipient is the same directory account, and would split the password from the mailbox (requirement 13).
10. **The object timeline has one row per follow-up**, updated in place. A row per attempt would flood the timeline exactly when someone is investigating (requirement 13).
11. **A cancellation holds until the declared state changes**, the same rule as Parked. Otherwise the next export would queue the cancelled change again (requirement 11).
12. **The retry policy is fixed for the first release**: backoff from 2 minutes to an hour, parked after 24 hours of failures, revisited after the integration tests (requirement 11).
13. **JIM warns about the GAL loop** rather than leaving it to the docs (requirement 23).
14. **Exchange gets its own Operations tab**, separate from Passwords, whose lifecycle differs (requirement 13).

## Open Questions

None. The portal mock's review settled decisions 6 to 14, and the PRD is locked for the implementation plan. The transport's go/no-go still waits on the spike's Phases 1b and 3 (see Dependencies).

## Acceptance Criteria

- [ ] A user provisioned with Exchange Recipient Type = Mailbox has a working mailbox, and its Exchange-stamped attributes import into JIM.
- [ ] Shared, room and equipment mailboxes are created on disabled accounts.
- [ ] A change of recipient type is reported on the Activity and changes nothing in Exchange.
- [ ] Deleting a mailbox user's account needs no Exchange step, and leaves a disconnected mailbox that Exchange can reconnect.
- [ ] Alias, primary SMTP address and external address changes reach Exchange.
- [ ] Exchange chooses the database when Exchange Database is empty, and the given database is used when it is set.
- [ ] A universal group can be mail-enabled; a non-universal group is refused with a clear error.
- [ ] Mail users and mail contacts are created with their external address.
- [ ] GAL sync between PANOPLY and RESURGAM works both ways: contacts carry the `X500:` proxy, none is projected back as a person, and each is deleted when its source leaves scope.
- [ ] A queued Exchange change is delivered within seconds of the export, survives a Worker restart, retries what a retry can cure, parks what needs a person, and never leaves JIM believing the recipient exists.
- [ ] A cancelled change is not queued again until the object's declared state changes.
- [ ] Follow-ups show in the Activities list, on the export item, in the Causality Explorer's Lineage and timeline, in Operations and on the object, as requirement 13 describes, and none appears at the top level of the Activities list.
- [ ] JIM warns when an importing Connected System's scope includes a container it provisions GAL contacts into.
- [ ] Re-running against a recipient already in its declared state changes nothing.
- [ ] Each export run logs summary statistics for its Exchange steps.
- [ ] Exchange settings appear only on an LDAP Connected System, are refused unless the directory is Active Directory, and need no realm, KDC or second account.
- [ ] Recipient provisioning can be set up, changed and stopped from the Exchange recipients panel, and the result is ordinary Attribute Flows.
- [ ] Settings, attributes and queued Exchange changes work from the portal, the REST API and PowerShell, with tests and docs.
- [ ] The Worker runs the Exchange step on its read-only root filesystem as a non-root user.
- [ ] The remoting library has no `JIM.*` references, enforced by test.
- [ ] Integration tests pass against the AD lab's Exchange SE servers.

## Additional Context

- **Phasing.**
  1. The spike, ending in a go/no-go.
  2. The first release (this PRD).
  3. Guided setup templates, building on the first release's Exchange recipients panel: a "Mailbox provisioning" template (recipient type, alias and SMTP patterns, database placement) and a "GAL sync" template (target OU, X500 proxy, loop-prevention scoping), both generating editable Attribute Flows.
  4. Later: leaver handling in Exchange (convert to shared, hide, disable, paired with JIM's leaver actions), mailbox permissions as entitlements, archives, linked mailboxes, policies.
- **Precedent.** The Password Delivery Service and its queue (`PendingPasswordChange`, its statuses and claim lease; `docs/configuration/initial-password.md`, `docs/concepts/password-synchronisation.md`) are the model for the Exchange queue. Whether the implementation generalises that lane's mechanics (claiming, backoff, parking, Activity reporting) to carry both kinds of work, or builds a parallel lane, is for the implementation plan; the password queue's secrecy invariants stay its own either way.
- **Already in place.** The `Mail Nickname`, `Proxy Addresses` and `Hide From Address Lists?` Metaverse Attributes and their LDAP Standard Mappings exist, and hiding a leaver from the address book already works through `msExchHideFromAddressLists`.
- **Deferred test: replication lag.** Requirement 8 (`-DomainController`) is what stops a just-created account being reported as "object not found" by a domain controller that has not replicated it yet. The AD lab has one domain controller per forest, so the integration tests prove the parameter is passed and honoured but cannot reproduce the lag itself. Proving that needs a second domain controller in PANOPLY, with JIM writing to one and Exchange's own choice being the other; not planned for now.
- **Scenario coverage** reviewed during the spike: <https://claude.ai/artifact/DcP86jG9dQsochAdpNUCFZ>
