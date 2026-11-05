# Exchange Mailbox Provisioning: Implementation Plan

- **Status:** Doing (Phases 1, 3, 4, 5 and 6 complete; Phase 2's settings check (2a), the Connector's delivery and the delivery hold (2b) done against the stand-in, quiet session close and the lab run next)
- **Issue:** [#2021](https://github.com/TetronIO/JIM/issues/2021)
- **PRD:** [`engineering/prd/doing/PRD_EXCHANGE_MAILBOX_PROVISIONING.md`](../../prd/doing/PRD_EXCHANGE_MAILBOX_PROVISIONING.md) (locked; requirement numbers below are the PRD's)
- **Transport spike:** [`engineering/plans/done/EXCHANGE_REMOTING_SPIKE.md`](../done/EXCHANGE_REMOTING_SPIKE.md)
- **Created:** 2026-11-01

## Overview

The PRD lets an administrator make JIM-provisioned Active Directory objects Exchange recipients by flowing a few connector-defined attributes ("Exchange Recipient Type", "Exchange Alias" and so on) on an export Synchronisation Rule. After a successful directory write, JIM queues a durable Exchange change, and a background delivery lane runs the Exchange cmdlets over the spike's remoting library. Each change is a follow-up of the export item that caused it (#2668), with retry and parking like the Password Delivery Service.

This plan maps every requirement onto JIM's existing code, sets the order of work, and records the design decisions the PRD left to it. It is written against `main` as of 2026-11-01, which includes #2668 (PR TetronIO/JIM#2677).

## Gate: the spike's go/no-go

Nothing below starts until the spike records a **go**. That needs the next lab window, about 30 minutes, approved in the lab VM session:

1. Re-run the read-only suite (`ExchangeLabTests`) against `ex1.panoply.local`. The framing fix it depends on (commits `3d98ec412`, `8c9d56a33`) has not yet met Exchange.
2. Run the write suite (`ExchangeLabProvisioningTests`, `JIM_LAB_ALLOW_WRITES=true`).
3. Record Q4 to Q6 in the spike plan: the Exchange-side failure catalogue (object not found, already enabled, non-universal group, insufficient RBAC, throttling), per-cmdlet timings, and whether serialisation payload signing affects the client.

Phase 5's failure classification and the concurrency bound (PRD decision 4) come from that run. A no-go stops this plan; the spike plan already says what happens then.

## Design decisions made in this plan

The PRD settled behaviour and left the mechanics here. The four marked **(confirmed)** change something the user can see or the PRD's wording; all four were agreed on 2026-11-01, and D7 is recorded in the PRD as decision 15.

### D1. A separate Exchange queue, on a shared delivery scheduler (confirmed)

The PRD asks whether to generalise the password lane or build a parallel one. The survey of the password lane shows the split cleanly:

- **Already kind-agnostic, reuse as is:** `AsyncWakeSignal`, `IDatabaseNotificationListener` (takes a list of channels), `ServiceHeartbeatWriter`, and `ProcessedExportItem.ExecutionItemId` with the #2668 follow-up parenting.
- **Generic in logic, password-named in types:** `PasswordDeliveryScheduler` (one lane per Connected System, bounded parallelism, hold-off, safety poll, all driven through `IPasswordDeliveryWork`). Generalise it into a `DeliveryScheduler` over a small work interface, and run one instance per kind.
- **Password-specific throughout, and staying that way:** the queue entity and its state machine, the claim/attempt/expire SQL, the trigger, and the secrecy invariants. The Exchange queue is its own table and its own state machine, written to the same patterns.

So: **a new `PendingExchangeChanges` table with its own NOTIFY channel, served by a second scheduler instance inside the same Worker delivery service**, reporting its own heartbeat so Service Health can show each lane (a new `JimService.WorkerExchange`). A fully generic queue would have to carry a password column that must never be read, or a payload column the password lane must never write; neither is worth the shared table. The PRD says the password queue's secrecy invariants stay its own, and this keeps them physically separate.

### D2. Connector-defined attributes are imported as well as exported

The five attributes (requirement 4) are added to Active Directory Object Types by the LDAP Connector's schema reader, flagged with a new `ConnectorSchemaAttribute.IsConnectorDefined`. Their names must be impossible as real `lDAPDisplayName`s, so a directory can never collide with them (for example `jim:exchangeRecipientType`, shown as "Exchange Recipient Type").

- **Export** never sends them to LDAP. The LDAP export strips them from the add or modify, and an update that changes nothing else makes no LDAP call. The export server needs nothing back from the Connector: staging (Phase 4) reads the declared values from the Pending Export it already holds.
- **Import** derives them from what Exchange stamped:
  - Exchange Recipient Type from `msExchRecipientTypeDetails` (and `msExchRecipientDisplayType` for contacts and groups);
  - Exchange Alias from `mailNickname`;
  - Exchange Database from `homeMDB`, as the database's name;
  - Exchange Primary SMTP Address from the `SMTP:` entry in `proxyAddresses`;
  - Exchange External Address from `targetAddress`.

This is what makes requirement 7 ("when declared state differs from its last imported state") and Scenario 7 ("nothing to do") fall out of JIM's ordinary export evaluation: a recipient that already matches produces no Pending Export change, so nothing is queued and Exchange is never contacted. It also gives export confirmation something to confirm against, so the change is confirmed only when an import shows Exchange has it (requirement 12).

**Confirmation waits for Exchange.** The directory write does not make a connector-defined change, so it must not be confirmed, retried or reported like one:
- **The hold.** Export marks each connector-defined change it hands to the queue `AwaitsExchange` (any decision but "not a recipient"), persisted with the batch. The confirming import holds such a change (not confirmed, not retried, no item) while the object has a queue row in any status, and judges it as any other change once it has none (the lane deletes a delivered row). An import that does not return the object judges a hold whose row has gone after reconciliation, against the last imported value, so a withdrawn or deleted row never strands it. Export evaluation and Drift Detection do not restate a held change, as for a change awaiting an Initial Password.
- **No optimistic apply.** Export never records a declared value on the Connected System Object; only an import changes one (requirement 12), so staging always reads what Exchange last stamped.
- **Re-staging is the same change.** A re-stage the queue finds unchanged writes no new follow-up. A Parked row with the same declared state is left Parked.

**An empty declared value lets Exchange decide.** Exchange chooses the database, Email Address Policies the primary SMTP address, and `mailNickname` gives the alias. No flow, export evaluation or drift correction stages a clearing change for a connector-defined attribute, and reconciliation confirms one that sets no value whatever the import shows. So an imported address that policy set never counts as a difference.

**Recipients JIM does not manage.** The import derives "Exchange Recipient Type" as `Unmanaged` for any `msExchRecipientTypeDetails` value outside the managed map (a linked or remote mailbox, a dynamic group), rather than leaving it absent, which would read as None. `Unmanaged` cannot be declared; declaring any type over it is refused, and None over it is nothing to do.

### D3. The domain controller written to travels on the export result

Today nothing above the LDAP Connector knows which domain controller a write went to. `OpenExportConnection` reads the RootDSE (including `dnsHostName`) and keeps only the directory type. Add `ConnectedSystemExportResult.DirectoryServer`, which the LDAP export fills from the bound connection's RootDSE `dnsHostName` (requirement 8). Every result of a connection carries the same value. Staging copies it onto the queued change, and the Exchange step passes it as `-DomainController`.

### D4. Exchange capability interface on the Connector

The Exchange cmdlets live in the LDAP Connector (PRD constraint), behind a capability interface the Worker discovers with `is`, like `IConnectorPasswordManagement`:

```csharp
public interface IConnectorExchangeRecipients
{
    Task OpenExchangeSessionAsync(IList<ConnectedSystemSettingValue> settings, CancellationToken ct);
    Task<ExchangeRecipientState?> ReadRecipientAsync(ExchangeRecipientTarget target, CancellationToken ct);
    Task<ExchangeConvergeResult> ConvergeAsync(ExchangeRecipientTarget target, ExchangeDeclaredState declared, CancellationToken ct);
    Task<ExchangeSessionCheck> CheckSessionAsync(IList<ConnectedSystemSettingValue> settings, CancellationToken ct); // the save-time check
    void CloseExchangeSession();
}
```

`ConvergeAsync` reads first and does only what converges (requirement 9). It returns a result classified from the library's `RemoteFailure` into the PRD's five failure kinds (requirement 26), never throws on a refusal, and never puts anything secret in a message. The models (`ExchangeDeclaredState`, `ExchangeRecipientState`, `ExchangeConvergeResult`, the failure-kind enum) live in `JIM.Models`. The library stays free of `JIM.*`, which its boundary test enforces.

### D5. Save-time warnings need a severity on settings validation

Requirement 3 needs a warning that does not refuse the save (a cmdlet the account's RBAC role does not allow). `ConnectorSettingValueValidationResult` has only `IsValid`. Add a `Severity` (Error or Warning). The portal shows warnings beneath the form after a successful save, and the REST API returns them in the update response, which PowerShell writes with `Write-Warning`. Every existing validator returns Error, so nothing else changes.

### D6. Discovery fills more than one setting

`IConnectorDirectoryServers` declares a single target setting. The Exchange server picker (requirement 1) needs a second "Discover" beside the Exchange server setting, listing `msExchExchangeServer` objects from the configuration partition. Widen the interface to a list of discoveries keyed by target setting (pre-release, so no compatibility shim). The portal's existing Discover button then works per setting.

### D7. Trust for an `https://` endpoint comes from JIM's certificate store (confirmed)

There is no certificate setting type. Rather than add one for a single field, an `https://` Exchange endpoint is validated against JIM's existing trusted certificate store (`IConnectorCertificateAware`), as LDAPS is. **This changes requirement 1's "CA certificate, shown only for an `https://` endpoint" into "trust the Exchange server's CA in Certificates"**, with the setting's help text saying so and the save-time check naming the missing certificate when it fails.

### D8. Kerberos configuration is generated per process, in the Worker and the web process

`Krb5Configuration` and `KerberosEnvironment` already exist in the library. **Amended 2026-11-04:** sessions open in the web process as well as the Worker, because saving Exchange settings runs the save-time check (D4, D5) where the portal and the REST API save them, as the LDAP connectivity check already does. So the configuration is per process, not per Worker, and both images carry MIT Kerberos and the environment PowerShell needs.

`ExchangeKerberosConfiguration` (in the LDAP Connector, which opens every session) is that process's one `krb5.conf`:
- a session asks for its realm and KDC just before it opens;
- realms are only ever added, so one a Connected System still uses never disappears from under its sessions;
- where two Connected Systems pin different domain controllers in one domain, both are listed as KDCs, the most recently asked for first;
- the file is rewritten only when what it says changes, in a private directory under the temporary path, which needs no restart (the spike confirmed it);
- `KRB5_CONFIG` is set once, before the first session.

This replaces the original design's Worker-side list of every Exchange-enabled Connected System, which the web process could not have kept and which a session's own request makes unnecessary.

Per requirement 2, the realm and KDC are derived, never entered. **Amended 2026-11-04 (Phase 2):**
- **Principal:** the configured Username (`DOMAIN\name`, UPN, DN or a bare name) is resolved through the directory to the account's entry, and the principal is its `sAMAccountName@REALM`. Not the UPN: a UPN whose suffix is not the realm (`svc-jim@panoply.com` in `PANOPLY.LOCAL`) would need Kerberos enterprise names.
- **Realm:** the account entry's `DC=` components, upper-cased. A `DOMAIN\name` naming another NetBIOS domain, or an entry outside the connected domain, is refused with that reason: a stated limit of the first version.
- **KDC:** the domain controller the LDAP bind reached (its rootDSE `dnsHostName`): the Preferred Domain Controller when one is set, otherwise whichever the Host resolved to. Neither the save path nor the lane's session open sees the pin, which lives in persisted connector data, so the pin as first designed could not be followed. Every domain controller is a KDC, so this is as valid, and the save check and the lane derive the same sign-in from the settings alone.

The resolution runs at save time (Phase 2), and its result is shown among the detected facts. What the check learns lives in the Connected System's own `SettingsCheckFacts`, written by the save that ran the check, because `PersistedConnectorData`, where the directory's facts live, is rewritten by every import.

### D9. Type mismatches and type changes become follow-ups without a queued change

- **Requirement 5** (a recipient type that does not fit the object) is checked during staging, where the object class and `groupType` are known.
- **Requirement 16** (a change of type on an existing recipient) is checked against the imported Exchange Recipient Type.

Neither is delivered. Each is staged as a terminal queue row with status Refused and the reason, which the lane never claims, and writes a follow-up Activity under the export item, CompleteWithError for a misfit and CompleteWithWarning for a type change, so it shows in the item's Follow-up section, the Lineage and the timeline with its reason (Scenarios 6 and 8). The row is what makes the refusal reported once: a later export re-staging the same declared state finds it unchanged and writes nothing. It holds until the declared state changes, or the refusal's cause goes (a group made universal), when the deliverable change supersedes it.

### D10. Retention: Exchange follow-ups use the general history period (confirmed)

The general retention pass excludes Password Synchronisation Activities, which are kept for 365 days. Exchange follow-ups get a new `ActivityTargetType.ExchangeRecipient` and fall under the **general 90-day period**, the same as the export items they hang from. That avoids the dangling "Export item no longer retained" link that password follow-ups show after 90 days. Parked queue rows are pruned by the same pass, after the same period.

### D11. Incremental delivery behind a feature flag (confirmed)

The work lands as eight pull requests, each merged to `main` when green, behind a new `ExchangeRecipients` feature flag (the #1781 lifecycle). The flag hides:
- the Exchange settings heading;
- the connector-defined attributes in schema import;
- the recipients panel;
- the Operations Exchange tab.

Tests run with it on. Once the integration scenarios pass, the last PR removes the flag. The alternative, one long-lived branch, would put a very large review and merge at the end.

## Implementation Phases

Each phase is one pull request, built test-first. Docs and a changelog entry go in the same PR wherever the phase is user-facing (while flagged, the changelog says so). Rough effort assumes one engineer and the spike's library as it stands.

### Phase 1: Library into production, the Worker and web images (2 days) ✅

- Reference `Tetron.PowerShellRemoting` from the LDAP Connector, and set `XDG_CACHE_HOME=/tmp/.cache` and `POWERSHELL_TELEMETRY_OPTOUT=1` in the Worker **and web** images (spike finding; the web image for D8's save-time sessions). The web image also gains MIT `libgssapi-krb5-2`, pinned to the Worker's version.
- Add `ExchangeKerberosConfiguration` (D8, per process). The library's own tests already pin the rendering (AES only, no reverse DNS, `0600`); the service's tests cover merging, rewriting only on change, concurrency and the private directory.
- Carry the spike's open items:
  - **TGT renewal:** an end-to-end harness test of a session outliving its ticket-granting ticket, both for a new connection and for a command running across the expiry. The harness KDC refuses service tickets for a TGT with about two minutes or less to run, so the tests use 150-second tickets. Both passed first time: the library signs in again as it stands, so the tests pin that rather than drive a fix.
  - **Large payloads:** harness tests from 1 KB to 5 MB either side of 16 KB, and a read-only lab test against ex1. Session statistics now report the largest message each way, so a test can prove it crossed 16 KB. The lab run (2026-11-04) showed Windows keeps sealed messages whole, and found that a command's parameters went in one request, which Windows refuses over 500 KB; the client now splits them (recorded in the spike plan), and Phase 2's lab run repeats the 1 MB case.
  - **No recorded fixtures (changed 2026-11-04):** a recorded Kerberos-sealed exchange can be replayed only with its session key, a secret, and a plaintext recording does not test the framing in question. The live lab test is the Windows proof, and can be re-run.
- **Moved out:** the feature flag (D11) shipped in Phase 3; quiet session close moves to Phase 2, since only ex1's front-end logs show the Ping 500s it is about.
- **Done when:** the Worker and web images, booted read-only as UID 1654, each open a harness session (`Test-ImageRemoting.ps1`, in `remoting-harness-tests`), and `remoting-harness-tests` is green in CI.

### Phase 2: Exchange settings, sign-in and detected facts (4 days; requirements 1 to 3)

In two pull requests. **2a (done):** the settings, D5's warnings on every surface, D6's discovery per setting, the
save-time check (`IConnectorSettingsCheck`, a new capability the server runs once the quick validation has refused
nothing) with its facts, and the stand-in Exchange endpoint in the harness, a restricted no-language session whose
commands an RBAC-limited account cannot see, against which the whole check runs in CI. **2b:** the Connector's
session and convergence (done), the delivery hold (done), then quiet session close and the lab run.

- **Settings:** add Exchange server (text, with the Discover action from D6) under the Exchange heading, shown only while Manage Exchange recipients is ticked (`RequiredWhenSetting`). The heading and the checkbox, and their refusal on a directory that is not Active Directory, came with Phase 3.
- **Refusals on save:**
  - An unreachable endpoint, or a sign-in Exchange refuses, each with the classified reason (clock skew named plainly).
- **Warning on save:** cmdlets missing from the session, named with the recipient types each affects (D5).
- **Detected facts:** the Exchange version, and the derived principal, realm and KDC.
- **Docs and coverage:** moved to Phase 8. COV-6 forbids a settings row for a setting behind an In Development flag, and a page about a feature customers cannot turn on would mislead them; they are written when the flag comes off.
- **Quiet session close** (from Phase 1): find what sends the Pings ex1's front end logged as HTTP 500 when the spike's sessions closed (the library sends none of its own), and close without them, proven by ex1's logs in this phase's lab run.
- **The lab write suite** (`LdapExchangeRecipientDeliveryLabTests`, category `RequiresExchangeLabWrites`, gated on `JIM_LAB_ALLOW_WRITES=true`): the harness suite's twin against ex1, through the Connector's own sign-in, reads and commands.
    - **Fixtures:** created through Exchange and then disabled, leaving plain directory objects, as the spike's provisioning suite did. That needs only svc-jim's Recipient Management role. svc-jim's directory delegation covers users and groups but not contacts, which a real deployment exporting mail contacts would need ([#2774](https://github.com/TetronIO/JIM/issues/2774)).
    - **Cases:**
        - each recipient type is enabled, and a re-run changes nothing;
        - an alias and primary address are set in a second session, and the Email Address Policy does not put its own address back;
        - a type change is refused;
        - a missing object is ObjectNotFound;
        - a missing database is refused, and the same object is then delivered.
    - **Quiet close:** each session's open and close times are written out, so ex1's IIS log can be matched to each close.
    - **The lab run** (approval through the lab agent) runs the read-only `RequiresExchangeLab` suite, which includes the 1 MB case, then this suite, then reads ex1's IIS log for `/PowerShell` over the run.
    - **First lab run (2026-11-05, 13:24 to 13:27Z):**
        - **Read-only suite:** 7 of 7 passed. The 1 MB case reached Exchange whole: a 132,606-byte request, answered by Exchange's own 128k filter-size limit, not a 413.
        - **Write suite:** stopped in setup, before any Connector session. `New-DistributionGroup -Type` is outside Recipient Management's role entry, which Exchange keeps in Security Group Creation and Membership. Without `-Type` the group is still universal.
        - **Clean-up:** it failed too. Exchange's `Remove-*` cmdlets remove recipients only, and the fixtures were still plain objects. Clean-up now removes each fixture as whatever it is at the end, making a plain one the simplest recipient of its kind first (`LabFixtureRemoval`). Six `jimlab*` objects were left for the nightly revert.
        - **The HTTP 500 at close:** it is the long-poll keep-alive `Ping`. When the client deletes the shell, the Ping is still waiting, and ex1 aborts it with `w:InternalError` ("The I/O operation has been aborted", FriendlyError=StreamProxy). The sequence in one shell was Ping, then Terminate, Command, Receive, Terminate, and Delete (200 each), and then the Ping ended with a 500. So quiet close means not having a Ping outstanding when the shell is deleted.
- **Tests:**
  - the validation severity, refusal and warning paths, with the session mocked behind D4's interface;
  - principal and realm derivation for all three username forms;
  - discovery parsing of `msExchExchangeServer`;
  - a harness test of the save-time check against the stand-in.
- **The LDAP Connector implements `IConnectorExchangeRecipients`** (2b, moved from Phase 5, which built the lane against the interface):
  - **Session.** One per lane, signed in with the same derived Kerberos sign-in as the save check (now one method). A sign-in the settings cannot give (no Exchange server, no account, an account the directory cannot find) is Refused, so its changes park for a person; credentials the directory refuses are AuthenticationFailed; an unreachable directory or Exchange retries.
  - **Converge** (`LdapExchangeConvergePlanner`, pure and table-tested). It reads first: Get-User, Get-Contact or Get-Group by object GUID (else DN), which finds the object whether or not it is a recipient yet, then Get-Recipient for alias and addresses. Every call names the domain controller the export used (requirement 8). None to a type runs the matching Enable-* (the mailbox kind as a switch, the database only when declared), then reads again and sets whatever still differs. The same type runs one Set-* with only what differs; a declared primary address turns `EmailAddressPolicyEnabled` off, or the policy puts its own back. An empty declared value is never compared or set: Exchange decides. A type change, or a recipient JIM does not manage, is refused. An enable someone else raced is read again and converged.
  - **Classification** (`LdapExchangeFailureClassifier`), from the remoting library's `RemoteFailure`:

    | Failure | Kind |
    |---|---|
    | Connection, timeout, protocol fault, untrusted certificate | Unreachable |
    | Authentication | AuthenticationFailed |
    | A command or parameter the account cannot see (RBAC hides rather than refuses) | InsufficientPermissions, naming the Recipient Management role group |
    | `ManagementObjectNotFoundException` naming the declared database | Refused (requirement 20: a missing database will not appear by retrying) |
    | Any other `ManagementObjectNotFoundException` | ObjectNotFound (usually replication) |
    | `ADServerUnavailableException`, `ADTransientException` | Unreachable |
    | Any other command failure | Refused, in Exchange's own words |

  - **Manage Exchange recipients off.** `IsManagingExchangeRecipients` lets the lane give such a system's changes back unattempted instead of failing them. That is the backstop; the hold (decided 2026-11-05) is what keeps the lane from claiming them at all:
    - **A typed column.** `ConnectedSystem.ManagesExchangeRecipients` is written on every settings save from the Connector's `IsManagingExchangeRecipients`, so the core never names an LDAP setting. Its migration backfills it from the setting.
    - **The lane holds without claiming.** The due list, the outlook, the claim statement and the queue summary leave a system that does not manage Exchange recipients out. Turning the switch back on lets its changes be claimed at their next due time; a held change keeps its retry clock.
    - **A Held state** (`ExchangeChangeState.Held`): a Pending change on such a system reads Held on the Operations tab (counted under Queued as "N more held", with its own filter), on the follow-ups, over REST and through `Get-JIMPendingExchangeChange -State Held`. It offers no Retry, and a retry that names it leaves it alone, since resetting its clock while nothing can be attempted helps no one. A Parked change on such a system can still be retried, and then waits, held.
    - **Saying so.** Saving with the switch off while changes wait returns a warning naming how many are held (portal, REST, `Set-JIMConnectedSystem`). The portal also confirms before a save turns the switch off, naming the schema-refresh trap: a refresh while it is off reports the Exchange attributes as removed, and only plain Apply keeps them. REST and PowerShell get the warning, not a gate: a confirmation is a portal affordance and their callers are scripts.
    - **Runtime check.** On the sandbox stack, switching off showed the confirmation and then the warning; the Operations tab and PowerShell read five changes as Held with no Retry; a retry naming the held change affected nothing; switched back on, the change read Retrying, took a retry and was claimed by the lane within seconds.
  - **Harness.** The stand-in Exchange keeps its recipients in a file every shell shares, so the Connector's harness tests enable, update, re-run (nothing changes) and fail (not found, hidden command, missing database, type change, wrong password, unreachable) against it end to end. A runtime check on the sandbox stack delivered a queued change through the real lane: one Kerberos login, four commands, and the change's Activity history read refused, retried, set.

### Phase 3: The flag, the Exchange setting and connector-defined attributes (4 days; requirements 1 in part, 4, 6, 25) ✅

Phase 3 runs before Phase 2, which needs the spike's go. It therefore also carries what the flag needs from Phase 2.

- **The flag** (D11): `Features.ExchangeRecipients`, In Development, removed by #2681. A Connector setting can name the flag it belongs to (`ConnectorSetting.FeatureFlagKey`); while that flag is off the setting is left out of the settings tab and the REST API's Connector Definition, and a request that sets it is refused. A value saved while the flag was on stays stored.
- **The Exchange heading and Manage Exchange recipients checkbox**, both flagged. Saving the checkbox turned on is refused unless the directory is Active Directory (Samba AD included in the refusal).
- **Schema:** the five attributes on Active Directory `user`, `group` and `contact` Object Types (only those that apply to each), flagged `IsConnectorDefined` (D2).
- **Export and import:** the export strips them; the import derives them. The import asks the directory for what each is derived from (an attribute name with spaces is not a valid one to search for), on Full Import, on the USN Delta Import (which now reuses the Full Import's attribute list) and when reading one entry by DN.
- **Only on Connected Systems that manage Exchange:** schema discovery adds the attributes only while Manage Exchange recipients is ticked, so nothing deeper in the engine checks the flag.
- **Contacts:** confirm that `contact` passes the Active Directory schema filter (`defaultHidingValue`), and make it selectable if not (requirement 6).
- **Tests:**
  - schema shape per class;
  - import derivation for every recipient type, from captured Exchange attribute sets;
  - export stripping on add and modify;
  - the flag gate on the REST API, the settings tab and the docs coverage rule (an In Development setting needs no public row).

### Phase 4: The queue and export staging (6 days; requirements 5, 7, 8, 11 to 14, 16) ✅

- **Moved from Phase 3, where staging needs them:**
  - shared, room and equipment mailboxes are created on disabled accounts (requirement 14). Active Directory creates an account disabled when no `userAccountControl` is given, so the export adds nothing. A Synchronisation Rule that flows an enabled account for one is refused for that object rather than overridden: an override would be found by the confirming import, and the next export would enable the account again (decided 2026-11-02);
  - the type-fit rules, as a pure function used by staging (requirement 5).
- **The `PendingExchangeChange` entity and migration:**
  - target: Connected System, Connected System Object and Metaverse Object;
  - declared state (the five values) and the `DomainController` (D3);
  - `Status` (Pending, Delivering, Parked, and Refused for D9's terminal refusals), failure kind, attempt count, `FirstFailedAt` (for the 24-hour park), `NextRetryAt`;
  - claim fields and the parent `ActivityId`;
  - A unique key on the Connected System Object gives "Superseded" by UPSERT, as the password queue does.
  - Column lists come from a new `PendingExchangeChangeBulkColumns`, guarded by `BulkInsertColumnCompletenessTests` and a round-trip test.
- **`ExportExecutionServer.StageExchangeChangesForBatchAsync`,** beside `StageInitialPasswordsForBatchAsync`:
  - reads the declared connector-defined values from each successful export's Pending Export, and applies D9's checks;
  - UPSERTs the changes;
  - writes one system-initiated Activity per change, parented by `NameExecutionItems` and `ParentRunProfileExecutionItemId` (#2668).
- **The trigger and the `jim_exchange_change` NOTIFY channel.**
- **Confirmation waits for Exchange** (D2): the `AwaitsExchange` mark and the import's hold and release, no optimistic apply of connector-defined values, no clearing change for an empty declared value, and `Unmanaged` recipients.
- **Withdrawal** moved to Phase 5. When the object or the declaring Synchronisation Rule is removed first (requirement 11's Withdrawn), the lane withdraws the change at claim time, as the password lane does. The queue already keeps the row with its references cleared, so the lane can find it.
- **Tests:**
  - staging per disposition (inserted, superseded, coalesced, parked-and-held);
  - D9's misfit and type-change follow-ups;
  - the `DomainController` captured from a two-server setup;
  - a `RequiresPostgres` test of the UPSERT and its unique key;
  - Scenarios 008 (Samba AD) and 032 (OpenLDAP) at Medium, since staging and reconciliation changed.

### Phase 5: The delivery lane (6 days; requirements 9 to 11, 26, 27, NFRs) ✅

Built ahead of the spike's go, against the capability interface rather than Exchange (agreed 2026-11-03): the lane, the queue's delivery statements, the host and its health are complete, and the LDAP Connector's own implementation of the interface arrives with Phase 2. Until then a lane on an LDAP system gives its claims back unattempted, which is harmless because staging is behind the flag.

- **The scheduler is shared (D1).** `PasswordDeliveryScheduler` became `DeliveryScheduler` over `IDeliveryWork`, with a `DeliveryService` base holding the readiness wait, the restart loop and the `NOTIFY` listener. The password lane's tests moved with it unchanged in substance.
- **`IConnectorExchangeRecipients`** (D4) holds what the lane needs: open a session, converge, the session's statistics, close. `CheckSessionAsync` and `ReadRecipientAsync` arrive with Phase 2, which first needs them. A session that cannot be opened throws `ExchangeSessionException` with its failure kind.
- **`ExchangeDeliveryServer`:**
  - claims due rows with `FOR UPDATE SKIP LOCKED` under a two-minute lease (twice the password lane's, since one converge can take seconds), in batches of 100, at most 1,000 per pass;
  - opens one session per lane (`MaximumSessionsPerConnectedSystem = 1`, raised once decision 4's measurement exists) and converges each change in order;
  - deletes delivered rows; every lane write is guarded on the row still being Delivering under that lane's claim, so a supersession or another deliverer's reclaim survives;
  - withdraws, without asking Exchange, a change whose account or declaring Synchronisation Rule was removed, or whose account's declaration has moved on since it was queued (below);
  - records a session that cannot be opened against every change claimed, so it shows against the person as Retrying (Scenario 5);
  - logs a per-lane summary with the session's Kerberos logins, connections and commands (requirement 27).
- **Retry and parking, corrected against the PRD.** This plan said Refused, Insufficient permissions and Authentication failures park at once. The PRD (requirement 11, decision 12) parks only on "Exchange refused, or 24 hours of retries failed". So Refused and Insufficient permissions park at once, because a person must act, and Object not found, Unreachable and Authentication failed retry from 2 minutes, doubling to an hour, and park once 24 hours have passed since the first failure, with the last attempt booked for the 24-hour mark.
- **Child Activities are written on change only (decided 2026-11-03).** A retrying change writes one on its first failure, whenever the failure kind or message changes, and on its final outcome (Delivered, Parked, Withdrawn). Repeats update only the queued row, which carries the latest attempt for Phase 6 to show. One per attempt, as the password lane writes, would be about 27 per change before parking, and about 27,000 during an outage over a 1,000-joiner batch.
- **The stale change is closed, from both ends.** Export evaluation kept a change awaiting Exchange when the declaration went back to what Exchange last stamped, because a sent change awaiting its confirming import is exempt from already-current withdrawal; its queued change would then have made a recipient nobody declares. A change awaiting Exchange now follows the Initial Password hold's rule: withdrawn unless the evaluation restates it exactly. The lane, in turn, reads each account's declared state now (its imported Exchange values with the changes still held for Exchange laid over) and withdraws a queued change that no longer matches it. A change still owed after that is queued again by the account's next export, because its released hold is judged unconfirmed and retried.
- **Health and retention:**
  - Service Health has a `WorkerExchange` service ("Worker · Exchange"), expected only while the flag is on and left out entirely while it is off; while off, the service runs no lane and writes no heartbeat. The portal's banners name it when it alone is down or stalled.
  - Retention follows D10: Parked rows past the general period are removed with the Activities they follow up; Refused rows are kept, because they stop a misfit being reported again. A removed Parked row releases its hold, so a change still owed is attempted once more after the general period.
- **Tests:** the state machine and backoff; the lane against a fake Connector (delivered, retrying, parked, the Activity rule, withdrawal three ways, a session failure, the lane stopped part-way, the pass bound); a `RequiresPostgres` fixture for the claim with two claimants, the lease, every guard, the outlook, retention and the per-object read; health with the flag on and off; retention.
- **Moved to Phase 2,** because they need the remoting library in production (Phase 1) or the spike's failure catalogue: the classification table from `RemoteFailure`, and the harness test with the stand-in server preloaded with fake `Enable-Mailbox` / `Get-Recipient`.

### Phase 6: Surfaces (6 days; requirements 4, 13, 28) ✅

- **No Cancel (decided 2026-11-03, PRD decision 16).** Retry is the only administrator action on a queued change. Phases 4 to 6a had carried a Cancelled status, its three columns and a `CancelExchangeRecipient` operation for this phase to use; nothing ever set them, and they are removed (a migration drops the columns) before the actions are built.
- **Follow-ups.** `ExecutionItemFollowUp` gains an Exchange outcome beside `PasswordOutcomes`. `ExecutionItemFollowUpDisplay` maps it ("Mailbox", "Shared mailbox", "Mail contact" and so on, with Queued, Delivering, Retrying, Parked, Delivered, Withdrawn, Replaced, Refused or Not made). The item page, the Lineage, the timeline and the Activity page then show Exchange follow-ups with no further work, because #2668 built them on that model.
- **Run-level summary.** The run's row in the Activities list and the run's own page sum its follow-ups by outcome (requirement 13, first bullet). This covers password follow-ups too; #2668 left it out.
- **Operations Exchange tab** (decision 14): summary cards, filters, a virtualised grid, and per-row and "shown" retry, modelled on `OperationsPasswordsTab`.
- **REST and PowerShell:**
  - REST: `GET api/v1/exchange/queue`, `queue/summary` and `POST queue/retry`.
  - PowerShell: `Get-JIMPendingExchangeChange` and `Resume-JIMPendingExchangeChange`, each with Pester tests and generated reference pages.
- **Connected System Object.** The object shows its declared recipient state beside the imported one, with any outstanding change and a link to it.
- **Exchange recipients panel** on the Attribute Flow tab (requirement 4): it lists the applicable attributes with their flows or default behaviour, and Set, Edit, Remove and Stop open the standard flow dialog or confirmation. It is portal-only by design. Built to the approved portal mock's screen 2, with today's button conventions:
  - **When it shows:** an export rule, the flag on, and an Object Type carrying the connector-defined Exchange attributes, which schema discovery adds only while the Connected System manages Exchange recipients. A group shows three rows, since the Connector never gives it Database or External Address.
  - **Set** opens the Add Attribute Flow dialog with the target fixed. **Edit** and **Remove** are the tab's own.
  - **Stop provisioning recipients** removes every Exchange flow, saved with the rule as every change on the tab is. Its confirmation says existing recipients are left as they are.
- **Tests:** bUnit tests for the panel and the tab, controller tests, and display-model tests for the new outcomes.

### Phase 7: GAL synchronisation (4 days; requirements 21 to 24)

- **The X500 proxy (requirement 22).** Import `legacyExchangeDN` into a new built-in Metaverse Attribute, "Legacy Exchange DN". The GAL sync rule's expression builds the `X500:` proxy from it. Check whether today's expression functions can build a multi-valued proxy list; add a function only if they cannot.
- **The GAL loop warning (requirement 23, decision 13).** It is raised when an importing Connected System's container scope covers a container that a Synchronisation Rule provisions contacts into. It is computed in the application layer, so the portal, the REST API (a `warnings` array on the scope and Synchronisation Rule responses) and PowerShell (`Write-Warning`) all show the same text.
- **Docs:** write `jim-ldap-connector-gal-sync.md`.
- **Tests:** the warning's containment logic, including OneLevel and Subtree scopes and exclusions, and the X500 expression.

### Phase 8: Integration, compliance and flag removal (5 days, plus lab time)

- **Scenarios:**
  - A new scenario for mailbox provisioning, run against the AD lab's `exchange-primary`: Scenarios 1, 2, 5 to 8 and the acceptance criteria.
  - A GAL scenario between PANOPLY and RESURGAM, which needs `exchange-source` (#2412).
  - Runs at Medium, so batching crosses a page boundary.
- **Docs and coverage** (from Phase 2): the Exchange server and Manage Exchange recipients rows on the LDAP Connector page (COV-6), the new `jim-ldap-connector-exchange.md` page, the RBAC role in `jim-ldap-connector-permissions.md` (the Recipient Management role group covers every command JIM needs), and the network requirements: the web container, not only the Worker, reaches the domain controller on 88 and Exchange on 80 or 443, since the save-time check signs in from it (D8).
- **Compliance:** SC-8 in `engineering/COMPLIANCE_MAPPING.md` moves from planned to in place.
- **Developer guide:** `engineering/DEVELOPER_GUIDE.md` gains the remoting library's boundary.
- **Flag removal:** the flag goes, and the PRD and this plan move to `done/` in the PR that closes #2021.

**Total: about 36 working days of build, plus lab windows.** The lab windows are the schedule risk, not the code.

## Success Criteria

The PRD's acceptance criteria, unchanged. Two are only provable on the lab: a working mailbox, and GAL sync both ways. One is deferred by the PRD itself: replication lag needs a second domain controller in PANOPLY.

## Risks

| Risk | Mitigation |
|---|---|
| The spike's re-run finds another framing difference | The decoder's diagnostics now identify a mismatch in one run; Phase 1 does not start until it passes |
| Export confirmation treats a queued-but-undelivered change as unconfirmed and re-exports it | D2: the import holds a change awaiting Exchange while its object has a queue row, and releases it once the row has gone; covered by Phase 4 tests |
| Exchange throttling under a large joiner batch | Concurrency set by measurement (decision 4); backoff and parking already cover throttling failures |
| `contact` is hidden by the Active Directory schema filter | Checked first in Phase 3; including it is a filter exception, not a design change |
| `exchange-source` is not ready for the GAL scenario | Phase 7's logic is unit-tested without it; only the end-to-end GAL run waits |
| Generalising the scheduler disturbs password delivery | The password lane's existing tests run unchanged against the generalised scheduler before any Exchange code uses it |

## Benefits

- Recipients follow from identity data, with no Exchange expertise in Attribute Flows, and Exchange keeps control of placement by default.
- A failed Exchange step is visible against the person, retried, and never hidden, through the same follow-up model as initial passwords.
- No Windows host or extra service: it runs in the Worker container, air-gapped.
