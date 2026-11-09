---
title: LDAP Connector Exchange Recipients
description: "Make the accounts, groups and contacts the LDAP Connector provisions into Active Directory into on-premises Exchange recipients."
type: object
audience: administrator
---

# LDAP Connector Exchange Recipients

On Active Directory, the LDAP Connector can make the objects it provisions into on-premises Exchange recipients: a mailbox for a joiner, a shared, room or equipment mailbox, a mail user, a mail contact for someone in another forest, or a mail-enabled group. Writing Exchange's own attributes to the directory creates no recipient; only Exchange can make one. So you declare what each object should be, with Exchange attributes that its export Synchronisation Rule flows like any other, and once the export has written the object, JIM asks Exchange to make it. Exchange keeps its own controls: it chooses the mailbox database and applies its Email Address Policies unless you say otherwise, and a failure in Exchange never undoes the directory write.

```jim-diagram
archetype: flow
title: How a declared recipient reaches Exchange
desc: "An export Synchronisation Rule flows the Exchange attributes, such as Exchange Recipient Type. The export writes the account to Active Directory, then queues one Exchange change for it, holding what is declared and the domain controller written to. Within seconds, the Worker's Exchange delivery service signs in to Exchange as the Connected System's account and makes the recipient, naming that domain controller. A failure that may clear by itself is retried; one that needs a person is parked. The next import reads back what Exchange stamped on the account, and only then does JIM treat the recipient as made."
in: { label: Export rule, note: flows Exchange attributes }
out: { label: Exchange recipient, note: read back by the next import }
steps:
  - { id: write, label: Directory write, note: the account is written first }
  - { id: queue, label: Exchange queue, note: one change per account }
  - { id: deliver, label: Exchange delivery, note: "retried, or parked for a person" }
```

## :material-lan-connect: Prepare the network and the account

JIM signs in to Exchange's remote PowerShell as the Connected System's own account, with Kerberos. There is no second account, and nothing about Kerberos to enter: JIM works the sign-in out from the directory settings.

- **The account**<br /> The **Username** on the Connected System, in any form it accepts (`CORP\svc-jim`, a user principal name or a distinguished name), resolved through the directory. It must be an account in the domain the Connected System connects to. JIM signs in as its `sAMAccountName` at the realm of that domain, so a user principal name with a different suffix still works.
- **The domain controller**<br /> JIM asks for its Kerberos ticket from the domain controller the directory connection reached: the **Preferred Domain Controller** when one is set, otherwise whichever the **Host** resolved to.
- **The connection to Exchange**<br /> By default, http, which Kerberos encrypts; JIM refuses a session it cannot encrypt. An `https://` address is used as TLS instead, and needs the certificate authority that issued the Exchange server's certificate added in [Certificates](../configuration/certificates.md), trusted for Connectors.

Both the web container (`jim.web`) and the Worker (`jim.worker`) sign in to Exchange: the web container when the settings are saved, because saving checks the sign-in, and the Worker when it delivers. Each needs to reach:

| Destination | Port | What for |
|---|---|---|
| The domain controller the Connected System connects to | 88 | The Kerberos ticket |
| The Exchange server | 80, or 443 for an `https://` address | Exchange's remote PowerShell |

Each also needs to resolve both names in DNS, and to keep its clock within five minutes of the domain's, or Kerberos refuses the sign-in.

The account needs a role in Exchange as well as its rights in the directory: [Exchange recipients](jim-ldap-connector-permissions.md#exchange-recipients) on the permissions page says which.

## :material-email-outline: Turn on Exchange recipient management

1. On the Connected System's **Settings** tab, under **Exchange**, tick **Manage Exchange recipients**.
2. In **Exchange server**, enter the Exchange server's own DNS name, such as `ex1.corp.local`, or use **Discover...** to choose from the Exchange servers Active Directory records. JIM uses `http://<server>/PowerShell/`; enter a full address instead for a load-balanced name or an `https://` endpoint.
3. Save. JIM signs in to Exchange to check the settings before saving them.
4. On the **Schema** tab, choose **Refresh Schema**, then select the Exchange attributes on the Object Types you provision, and the `contact` Object Type if you provision mail contacts.

Saving is refused when the sign-in fails, with the reason (an unreachable server, a password the domain refuses, a clock out of step), and nothing is saved: the Connected System keeps the settings it had. Saving is refused too on a directory that is not Active Directory, Samba AD included, because Exchange runs only against Active Directory. When the account signs in but Exchange's roles do not let it run every command JIM needs, the save goes ahead with a warning naming the commands and the recipient types they affect; changes of those types park until the account can run them.

A successful save records what it found among the Connected System's [Detected facts](../configuration/connected-systems.md#detected-facts): **Exchange**, the Exchange version, and **Exchange Sign-in**, the Kerberos sign-in JIM worked out.

Schema discovery adds the Exchange attributes only while **Manage Exchange recipients** is ticked, so refresh the schema after ticking it, and only while it is ticked.

## :material-account-plus-outline: Provision recipients

An Object Type of a Connected System that manages Exchange recipients carries these attributes. They exist only in JIM: the export never writes them to the directory, and the import derives them from what Exchange stamped on the object.

| Attribute | On | What it declares | When no Attribute Flow sets it |
|---|---|---|---|
| Exchange Recipient Type | `user`, `group`, `contact` | `None`, `Mailbox`, `SharedMailbox`, `RoomMailbox`, `EquipmentMailbox`, `MailUser`, `MailContact` or `MailEnabledGroup` | The object is not a recipient |
| Exchange Alias | `user`, `group`, `contact` | The recipient's alias | Exchange uses `mailNickname` |
| Exchange Database | `user` | The mailbox database, by name | Exchange chooses |
| Exchange Primary SMTP Address | `user`, `group`, `contact` | The primary email address | Email Address Policies decide it |
| Exchange External Address | `user`, `contact` | Where a mail user's or mail contact's mail goes | Required for those two types |

On the Attribute Flow tab of an export Synchronisation Rule for such an Object Type, the **Exchange recipients** panel lists the attributes that apply, each with the Attribute Flow that sets it or what happens without one. **Set up recipients** starts with Exchange Recipient Type; **Set**, **Edit** and **Remove** open the ordinary Attribute Flow dialog. The panel stores nothing of its own: what it sets up are ordinary Attribute Flows, which the REST API and PowerShell create as they create any other ([`New-JIMSyncRuleMapping`](../powershell/attribute-mappings.md#new-jimsyncrulemapping)).

### Declare the recipient type

Flow a constant where every object the rule provisions gets the same kind of recipient (`"Mailbox"`), or an expression where it depends on the person: `IIF(Eq(mv["Employee Type"], "Contractor"), "MailUser", "Mailbox")`. An expression that gives no value declares `None`.

Each type fits one kind of object, and JIM refuses one that does not fit before asking Exchange: a mailbox or mail user on anything but a `user`, a mail contact on anything but a `contact`, and a mail-enabled group on anything but a universal group. The refusal is reported against the object, and nothing changes in Exchange; the directory write the export made stands.

Shared, room and equipment mailboxes live on disabled accounts. Active Directory creates an account disabled when no `userAccountControl` is given, so flow none for them, or flow a disabled value; JIM refuses to create one on an account the rule flows as enabled.

### Choose a mailbox database

Leave **Exchange Database** without an Attribute Flow and Exchange's automatic mailbox distribution chooses, honouring the databases you have excluded or suspended from provisioning. To place mailboxes by rule, flow the database's name: `IIF(Eq(mv["Department"], "Executive"), "MDB-VIP", null)` puts executives in `MDB-VIP` and leaves everyone else to Exchange. A database that does not exist is refused by Exchange and the change parks, as [Troubleshooting](#troubleshooting) describes.

### Set addresses

Leave **Exchange Primary SMTP Address** without an Attribute Flow and Exchange's Email Address Policies give each recipient its addresses. Flow one only to override the policy: JIM then turns the policy off for that recipient, or Exchange would put the policy's address back.

The same goes for `mail`. Exchange writes `mail` from the recipient's primary address, so an Attribute Flow to `mail` on a mailbox and the Email Address Policy both write it, and they agree only when the flowed address is exactly the one the policy gives. Otherwise each export changes it back. Do not flow `mail` on recipients unless you set their primary address deliberately, through Exchange Primary SMTP Address. A mail user or mail contact is the exception: Exchange writes its external address to `mail`, so where the same Synchronisation Rule flows `mail` to other objects, flow a mail user's or mail contact's external address to it, or the confirming import reports the export unconfirmed.

**Exchange External Address** is required for a mail user or mail contact; one declared without it is refused and parks until an Attribute Flow gives it one. Write the bare address (`first.last@partner.example`).

Mail contacts for people in another forest are the usual reason to provision contacts at all; [Synchronise Global Address Lists Between Forests](jim-ldap-connector-gal-sync.md) sets that up end to end.

## :material-pencil-outline: Change or stop provisioning

- **Change an alias or address**<br /> Change what the Attribute Flow gives. The next export queues the change, and Exchange updates the recipient.
- **Change a recipient's type**<br /> JIM does not convert an existing recipient into another type, or back into a plain object. The change is reported against the object as not made, and nothing changes in Exchange. To convert one, convert it in Exchange itself and change the declaration to match; the next import reads the new type back.
- **Stop provisioning recipients**<br /> In the **Exchange recipients** panel, **Stop provisioning recipients** removes the rule's Attribute Flows to Exchange attributes once you save the rule. Existing recipients are left as they are: their declared type becomes `None`, which is reported against each and not made. Objects the rule provisions from then on get no recipient.
- **Delete the account**<br /> When the Synchronisation Rule's Deprovisioning Action deletes a recipient's account, JIM runs nothing in Exchange. Exchange disconnects a deleted user's mailbox itself and keeps it for its database's deleted mailbox retention period (30 days unless your Exchange administrators changed it), during which an Exchange administrator can reconnect it. Other recipients go with their object.
- **Turn management off**<br /> Untick **Manage Exchange recipients** and save. Nothing is discarded: the changes already queued, and those later exports queue, wait as **Held**, unattempted, and are delivered once it is ticked again. The portal asks before a save turns it off; the REST API and `Set-JIMConnectedSystem` warn with how many changes are held. While it is off, do not refresh the schema: a refresh then reports the Exchange attributes as removed. If one does, choose **Cancel** in its preview, which leaves the Synchronisation Rules and their Attribute Flows as they are.

## :material-tray-full: Follow a change to Exchange

After each successful directory write, JIM compares what is declared with what the last import read back from Exchange. Where they differ, it queues one Exchange change for the account, holding the declared state and the domain controller the export wrote to. A recipient already in its declared state queues nothing, and Exchange is not contacted for it. A newer declaration for the same account replaces a change still waiting, so only the latest is ever delivered.

The Worker's Exchange delivery service picks a change up within seconds of the export, without waiting for another run. It serves each Connected System over one Exchange session, reads each recipient first, and does only what brings it to its declared state. It names the domain controller the export wrote to, so Exchange does not look for a new account on a domain controller that has not received it yet.

```jim-diagram
archetype: decision
title: What JIM does when an Exchange attempt fails
desc: "When an attempt fails, JIM first asks whether a retry can cure it. A refusal from Exchange, such as a database that does not exist, or a command the account's roles do not allow, cannot be cured by retrying, so the change is parked at once and waits for a person. Anything else, such as an object Exchange cannot see yet, an unreachable server or a refused sign-in, is retried: two minutes after the first failure, the wait doubling each time to at most an hour. A change that has been failing for 24 hours is parked."
start: { label: A failed attempt }
questions:
  - { id: cure, label: "Can a retry cure it?", sub: "not a refusal or a missing role" }
  - { id: day, label: "Failing for 24 hours?" }
outcomes:
  - { id: parked, label: Parked, sub: waits for a person }
  - { id: retrying, label: Retrying, sub: "two minutes, doubling to an hour", good: true }
  - { id: expired, label: Parked, sub: after 24 hours of retries }
answers:
  - { from: cure, to: parked, label: "no" }
  - { from: cure, to: day, label: "yes" }
  - { from: day, to: retrying, label: "no" }
  - { from: day, to: expired, label: "yes" }
```

Each queued change stands in one of these states:

| State | What it means | What to do |
|---|---|---|
| Queued | Waiting for its first attempt | Nothing |
| Delivering | Exchange is applying it now | Nothing |
| Retrying | A failure that may clear by itself; JIM tries again at the next attempt time | Nothing, or **Retry** once the cause is fixed |
| Parked | JIM has stopped trying: Exchange refused it, the account lacks the role, or failures went on for 24 hours | Fix the cause, then **Retry** |
| Held | Its Connected System does not manage Exchange recipients | Tick **Manage Exchange recipients** |
| Refused | JIM refused the declaration before asking Exchange, such as a type that does not fit the object | Change what is declared |
| Not made | It would change an existing recipient's type | Change what is declared, or convert the recipient in Exchange |

A change cannot be cancelled. To stop or correct one, change what is declared (the source data or the Attribute Flow), and the newer state replaces it. A change nobody is owed any more, because its account or its Synchronisation Rule was removed or the declaration moved on, is withdrawn without asking Exchange. A failed change never rolls back or hides the directory write: the account stays as the export wrote it, and JIM treats the recipient as made only once an import reads it back. Run an import after the export, as you would to confirm any export.

Where to watch it:

- **The export's execution item**<br /> Lists the change under [Follow-up](../configuration/activity-execution-items.md#follow-up), as "Mailbox", "Mail contact" and so on, with where it stands now and a link to its own Activity, which records each failure, retry and outcome. A follow-up never changes its run's status.
- **The Connected System Object**<br /> Its **Exchange Recipient** panel shows what is declared beside what the last import read back from Exchange, with any change still owed and its **Retry**.
- **Administration > Operations**<br /> The **Exchange** tab lists every change not yet delivered, across every Connected System, badged with how many wait for a person. Filter by status or failure reason, and retry one change or **Retry shown**. The **Worker · Exchange** card on [Service Health](../configuration/operations.md#service-health) says whether the delivery service is running.
- **PowerShell and the REST API**<br /> [`Get-JIMPendingExchangeChange`](../powershell/exchange-queue.md#get-jimpendingexchangechange) reads the queue, and [`Resume-JIMPendingExchangeChange`](../powershell/exchange-queue.md#resume-jimpendingexchangechange) retries; the REST API serves the same queue under `/api/v1/exchange`.

A retry makes a parked or retrying change due at once, with its attempts and its 24-hour clock reset. It leaves a held change waiting, and a refused or not-made change waits for its declaration to change.

The Worker's log records each delivery pass per Connected System: how many changes were delivered, retrying, parked and withdrawn, and the Exchange session's Kerberos sign-ins and commands.

## :material-tune-variant: Settings

These are LDAP Connector settings, under the **Exchange** heading of the Connected System's **Settings** tab, and appear only on an LDAP Connected System.

| Setting | Default | Effect |
|---|---|---|
| Manage Exchange recipients | `false` | Active Directory only. Makes the objects this Connected System provisions Exchange recipients, as their Synchronisation Rules declare, and adds the Exchange attributes to its schema. Saving it on signs in to Exchange to check the settings. Turned off, queued changes are held, not discarded |
| Exchange server | None; required when Manage Exchange recipients is on | The Exchange server's DNS name, which JIM reaches at `http://<server>/PowerShell/`, or a full `http://` or `https://` address. **Discover...** lists the Exchange servers Active Directory records. An `https://` address needs its issuing certificate authority in Certificates |

## :material-wrench-outline: Troubleshooting

### Saving is refused with "The domain has no service principal HTTP/..."

- **Cause**<br /> Kerberos finds a server by the name JIM is given, and the domain knows the Exchange server by its own DNS name, not by an alias or an IP address.
- **Fix**<br /> Enter the Exchange server's own DNS name in **Exchange server**.
- **Check**<br /> Save again; the save goes ahead, and the Detected facts show the Exchange version.

Nothing is changed by a refused save: the Connected System keeps its previous settings.

### Saving is refused because the clock differs by more than five minutes

- **Cause**<br /> Kerberos refuses a sign-in from a server whose clock is out of step with the domain's.
- **Fix**<br /> Correct the time on the server running JIM's containers, or on the domain controller the message names, then save again.
- **Check**<br /> The save goes ahead.

### Saving is refused because JIM could not reach the domain controller on port 88, or Exchange

- **Cause**<br /> The web container cannot reach the domain controller's Kerberos port, or the Exchange server's port 80 or 443. The Worker needs the same routes to deliver.
- **Fix**<br /> Allow both routes from the web container and the Worker, as [Prepare the network and the account](#prepare-the-network-and-the-account) lists.
- **Check**<br /> Save again. A change already queued and retrying is delivered at its next attempt.

### Saving warns that the account cannot run some commands

- **Cause**<br /> The account signed in, but its Exchange roles hide commands JIM needs. Exchange hides a command an account may not run rather than refusing it.
- **Fix**<br /> Add the account to Exchange's Recipient Management role group, or give it a role with the commands the warning names; see [Exchange recipients](jim-ldap-connector-permissions.md#exchange-recipients).
- **Check**<br /> Save again with no warning, then retry the changes parked for insufficient permissions: `Resume-JIMPendingExchangeChange -State Parked -FailureKind InsufficientPermissions`.

The settings were saved; only changes of the affected types park until the role is granted.

### A change parks because a database was not found

- **Cause**<br /> Exchange Database names a database Exchange does not have. Retrying will not make it appear, so JIM parks the change at once.
- **Fix**<br /> Correct the Attribute Flow, or the data it reads, so it names a database that exists; the corrected declaration replaces the parked change. If the database should exist, create it in Exchange and retry the change.
- **Check**<br /> The change leaves the Exchange tab, and the account's Exchange Recipient panel shows the mailbox after the next import.

The account stays as the export wrote it, without a mailbox, until then.

### A change is retrying because Exchange could not find the object

- **Cause**<br /> Exchange looked for the account and did not find it: usually the account has not yet replicated to where Exchange looked. The failure reason reads Object not found, with Exchange's own words.
- **Fix**<br /> Usually nothing: replication catches up and a later attempt succeeds. If it goes on, check that the Exchange server can reach the domain controller the Connected System writes to, and that the account still exists.
- **Check**<br /> The change is delivered at a later attempt; one still failing after 24 hours parks, and **Retry** starts it again once the cause is fixed.

### The export fails with "A SharedMailbox must be created on a disabled account"

The same message names `RoomMailbox` or `EquipmentMailbox` for those types.

- **Cause**<br /> The Synchronisation Rule flows an enabled `userAccountControl` for a shared, room or equipment mailbox, which lives on a disabled account.
- **Fix**<br /> Flow a disabled value for those types, or no `userAccountControl` at all, so Active Directory creates the account disabled.
- **Check**<br /> The next export creates the account, and its Exchange change is queued.

Nothing was created: the export refuses the account before writing it.

### A mail user or mail contact is refused for want of an external address

- **Cause**<br /> Exchange Recipient Type declares `MailUser` or `MailContact`, and no Attribute Flow gives Exchange External Address a value for the object.
- **Fix**<br /> Flow Exchange External Address for those types.
- **Check**<br /> The next export queues a change, and it is delivered.

### Exchange's web server log shows HTTP 500 responses to JIM's sessions

- **Cause**<br /> These are the session's keep-alive request ending: once when each session closes, and each time an idle session's request reaches its 20-second timeout, after which JIM sends another. Exchange records both as HTTP 500, exactly as it does for Windows PowerShell's own client.
- **Fix**<br /> None is needed; they are not failures, and the changes those sessions carried are delivered.
- **Check**<br /> The delivery pass's log line in the Worker reports the changes as delivered.

## :material-book-open-variant-outline: Related

- [Synchronise Global Address Lists Between Forests](jim-ldap-connector-gal-sync.md): mail contacts for each forest's people and groups in the other.
- [LDAP Connector Service Account Permissions](jim-ldap-connector-permissions.md): the directory rights and the Exchange role the account needs.
- [Exchange Queue](../powershell/exchange-queue.md): reading and retrying queued changes from PowerShell.
- [Attribute Mappings](../configuration/attribute-mappings.md): the Attribute Flows that declare each recipient.
- [JIM LDAP Connector](jim-ldap-connector.md): every setting the Connector takes.
