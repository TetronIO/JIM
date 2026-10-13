---
title: Password Synchronisation
description: How one password change reaches every system a Metaverse Object is connected to, queued and retried per system, and how to watch it.
type: concept
audience: administrator
---

# Password Synchronisation

Password Synchronisation takes one password change to **every** system a Metaverse Object has a Connected System Object in, durably, without you standing over it. Setting a password on one Connected System Object, at the moment you ask, is the other half, covered in [Passwords](passwords.md).

--8<-- "assets/diagrams/password-synchronisation.svg"

<p class="jim-diagram-caption">Each system gets its own queued change, so none of them waits on another and one unavailable system cannot fail the rest.<span class="jimdg-caption-motion"> Moving dots trace one password change fanning out.</span></p>

You configure it per Connected System, on the **Passwords** tab of the Connected System, and it appears only on systems whose connector can set passwords at all. Two settings, and one deliberate separation between them:

- **The configuration** says which Object Type receives passwords, how many delivery attempts to make before JIM stops and asks you to look, how long to wait before the first retry, and whether to refuse to transmit over a connection JIM cannot confirm is encrypted.
- **The enable toggle** is separate from the configuration existing, so you can set a system up ahead of a change window and switch it on during one. A configured system that is switched **off** does not discard password changes: they accumulate, and switching it on delivers what accumulated.

That is also why there is no way to remove a configuration, only to disable it. Removing one would throw away everything queued against it.

How long a change waits before JIM gives up on it is the Connected System's **initial password time to live** setting, shared with initial password provisioning: the question both are asking is how long that system may be unavailable before JIM stops trying, and the answer is a property of the system.

## ▶️ Setting a password

JIM has one operation for giving somebody a password, **Set Password**, and you aim it one of two ways. Both go through the same queue, the same delivery service, the same retries and the same history; what differs is where the password goes, and so what the sensible defaults are.

| | **Named Connected System Objects** | **Every configured system** |
|---|---|---|
| Answers | "Change this person's password in the systems I choose" | "This person's password changed; every system should hold it" |
| Reaches | The Connected System Objects you name, whether or not their system's Password Synchronisation is switched on | Every Connected System configured for Password Synchronisation, including those switched off (held) and those where the Connected System Object does not exist yet (delivered when it does) |
| Expiry, unless you say otherwise | Change required at next sign-in: somebody else chose this password | Left to each system's own policy: the person chose it, and should not be made to choose another |
| Told to you | The call waits up to ten seconds and reports what each Connected System Object did with the password | The call returns as soon as the change is recorded; ask it to wait if you want the outcomes |
| Enable the Connected System Object | Available | Never by choice: a propagated password reaches Connected System Objects an administrator may have disabled on purpose. It only finishes enabling a new Active Directory account that was disabled until it had a password |
| "Password never expires" (Active Directory and Samba AD) | Set for **Never expires** and cleared for the other two, as you choose; where a Synchronisation Rule writes the account's `userAccountControl` with Enforce State on, the next synchronisation puts that flow's value back, and the outcome warns you | Left as each Connected System Object has it, so **Never expires** needs named Connected System Objects and is refused without them |

Name the Connected System Objects when you are choosing the password for somebody: onboarding them, or putting right one whose password was refused or forgotten. Name none when they have already changed their own password somewhere and the rest should catch up; this is also the shape a future capture agent, replaying a change made in another directory, would use.

In the portal, both live on the Metaverse Object's **Password** tab: the **Set Password** card lists its Connected System Objects, and what is still to be delivered and what recent changes did sit beneath it. Both are available to automation:

```powershell
# Change the password on the Connected System Objects in the systems you name, and wait to hear what each did with it
Set-JIMMetaverseObjectPassword -Id $id -ConnectedSystemId 3 -Password $password

# Propagate a password change to every configured system, returning as soon as it is recorded
Set-JIMMetaverseObjectPassword -Id $id -Password $password

# The same, but stay on the line for up to ten seconds and be told which systems took it
Set-JIMMetaverseObjectPassword -Id $id -Password $password -Wait 10
```

Over REST it is one endpoint, `POST /api/v1/metaverse/objects/{id}/password`, with `connectedSystemObjectIds` naming the Connected System Objects or omitted to propagate. `POST /api/v1/synchronisation/connected-systems/{connectedSystemId}/connector-space/{csoId}/password` is the same operation with that one Connected System Object named, for callers that hold the object rather than the Metaverse Object. Every endpoint that accepts a password refuses the request unless JIM can confirm the connection is encrypted; if TLS terminates at a reverse proxy, set `JIM_TRUSTED_PROXIES` so JIM reads the forwarded scheme rather than the hop it can see.

Either way the change is recorded and delivered separately (the next section says why): the call answers once the change is durable, and the first delivery attempt follows within about a second. The endpoint takes an optional `wait`, in seconds from 0 to 30 (`-Wait` in PowerShell), which overrides either default and holds the request until every target has settled or the time runs out. It answers `200` when everything settled and `202` when something was still on its way, with the same body either way: `origin` (`Explicit` or `Propagated`), `settled`, and one entry per Connected System carrying its `state` (`Queued`, `Delivering`, `Set`, `Retrying`, `Parked`, `Held`, `Expired` or `Cancelled`), the target's own `message`, its `attemptCount`, and `nextAttemptAt` for a target that is retrying. A target that is retrying counts as settled: its next attempt is minutes away, and nobody at a screen should be held for it. A target that was parked is settled too, and is the one that needs you: the system refused the password, in the words the `message` carries, and sending the same one again would be refused the same way.

## 📬 How a password change reaches a system

A password change is recorded first and delivered afterwards, never in the same breath. The person changing their password must not be held waiting on a directory, and their new password must not fail to take because one of the systems they have a Connected System Object in happens to be down. So JIM writes one queued change per target system, encrypted, and returns; the **Password Delivery Service** takes it from there.

What happens to a queued change:

- **It is delivered, and disappears.** While it is being written to the target it shows as **Delivering**; the moment the target has the password the change is gone, because there is no value worth retaining and every reason not to. The outcome is recorded as an Activity, which is where the Metaverse Object's password history comes from.
- **It is retried.** A target that was unreachable, or that failed in a way another attempt may resolve, gets one. Each wait is twice as long as the one before it, starting from the backoff you configured, and never longer than the time the change has left.
- **It waits for an export to the account to finish.** Where an export to the account is still being sent, any delivery except one where you chose the enabled state yourself waits and tries again about 15 seconds later. The export may be disabling the account (a leaver, say), may be the Attribute Flow that owns its flags, "Password never expires" included, or may be enabling one that has no password yet, which Active Directory refuses, so JIM holds that enable back for this delivery to complete; JIM records what an export did only once it has finished. Waiting uses none of the change's attempts. An enable you choose when you set a password on one Connected System Object does not wait. An export still being sent after an hour has almost certainly been left behind by a Worker that stopped part-way through it, so JIM stops waiting for it and logs a warning naming it.
- **It is parked, and waits for you.** A target that *refused* the password, or that cannot do what was asked at all, will refuse it identically next time; JIM stops rather than burning the attempts. So does a change that has used all of them. Parked work is released, and tried again, the moment you change what would be delivered to that system: switching Password Synchronisation on, or correcting a setting.
- **It is held, because the system is switched off.** Password Synchronisation being switched off on a Connected System does not stop changes being recorded for it; it stops them being sent. They accumulate, shown as **Held**, and switching the system back on delivers all of them without anything else being done. Nothing about a held change is attempted while it waits, so it does not consume attempts and does not appear in the due count. It still expires on time, which is what bounds how long a change window can last before the passwords made during it are lost.
- **It expires.** A change that outlives its time to live is retired with its last failure recorded, rather than delivering a password the person may have changed twice since.

A change for someone who changes their password again before the first one is delivered replaces the first, rather than queueing behind it. Only the newest password is ever sent.

An [initial password](passwords.md#giving-new-connected-system-objects-their-first-password) queued by an export travels the same way, with one difference: the queued change carries no password value at all, because it never leaves the Connected System Object's Synchronisation Rule. JIM resolves what to send from that rule's Initial Password settings at each attempt, so a later change to those settings is picked up by the very next attempt rather than only by the Connected System Object's next export.

## ⚡ The Password Delivery Service

Delivery is not a synchronisation task and never waits behind one. The Worker runs a separate Password Delivery Service alongside its synchronisation loop, and the two share nothing but the process: a change queued while a Full Import is running is attempted within about a second of being queued, and a retry is attempted when it falls due rather than when the Worker next has nothing to do. That is what "on its own clock" means: the service's clock, not the synchronisation engine's.

The service is woken by the queue itself. Recording a change wakes it, and so does anything that makes a change due: switching Password Synchronisation on for a system (to deliver what accumulated), a retry from the Passwords tab or from PowerShell, a correction to a system's delivery settings, and a retry falling due, which the service has already set a timer for. A sweep every 30 seconds sits under all of that as a floor, so nothing waits longer than that even if a wake-up is lost.

Each Connected System is delivered to on its own, one change at a time over a single connection, with up to four systems in progress at once, so a directory that is slow or down delays only its own passwords. A system the service could not reach at all is left alone for 30 seconds before it is tried again, rather than being hammered while it is down; a new change or a retry for that system lifts the pause immediately. A system that is switched off is never attempted: its changes are held rather than due, so nothing is done on their account until you switch it on.

A change the service has picked up is marked **Delivering** for as long as the write takes. If the Worker stops mid-write, the change is not lost and not stuck: the claim lapses after a minute and the change is picked up again, and sending a password a second time is harmless.

The service reports its own health. Its **Worker · Passwords** card on the [Service Health](../configuration/operations.md#service-health) strip says whether it is running, which systems it is delivering to, and what the queue holds ahead of it: how many changes are due now, how many are waiting out a retry, and when the next attempt is.

## 🔎 Watching the queue

Delivery works on its own, which is exactly why you need somewhere to look when it does not. The **Passwords** tab of **Administration > Operations** lists every change on its way to a Connected System, one row per Metaverse Object per system, with what the target said about it. It sits beside the Queue, History and Schedules tabs because it answers the same question they do: what JIM is doing, and what it has stopped doing. The tab is badged with how many changes are waiting on a person (parked plus expired), so a backlog is visible from anywhere on the Operations page.

An [initial password](passwords.md#giving-new-connected-system-objects-their-first-password) queued by an export appears here too, labelled with origin **Initial** alongside **Set** and **Propagated**, because it is the same queue, the same delivery service and the same outcomes as every other password change.

It never shows a password, and cannot: the queued value is encrypted in the database and has no representation on any page, in any API response, or in any log line.

Four counts sit above the list:

- **Waiting**<br /> Changes JIM still intends to deliver, including any being delivered at this moment. The second line says how many of those are due now; the rest are waiting out a retry backoff, or are held because their Connected System is switched off. A large waiting count with nothing due is a queue working through its backoffs, or one waiting on a system to be switched back on. A due count that stays large is a queue that is not being drained: check the **Worker · Passwords** card on the Service Health strip above the tabs.
- **Parked**<br /> The target refused them, or they ran out of attempts. These wait on you.
- **Expired**<br /> They outlived their time to live. The password each carried is gone, so nothing can deliver them now.
- **Cancelled**<br /> You stopped them. Counted rather than hidden, because that person's password is still divergent on that system and the count is the only thing that says so.

Filter by Connected System, by state, or by how the last attempt failed, and search by Metaverse Object or system. Two actions apply to whatever the filters are currently showing, as well as to a single row:

- **Retry**<br /> Makes matching changes due immediately; the Password Delivery Service attempts them within about a second. This is what you run once the reason a directory was refusing passwords has been dealt with. It applies to waiting, parked and cancelled changes; an expired one is left alone, because there is no password left to send.
- **Cancel**<br /> Stops JIM delivering them. The changes stay, marked **Cancelled**, recording who cancelled them and when.

!!! note "Cancelling records an outcome; it does not erase one"
    A cancelled change is kept for the same reason an expired one is: that person's password on that system is now out of step with the rest, and deleting it would leave you believing your systems agree when they do not. Retention removes cancelled changes on the same schedule as any other finished change (see [How long any of it is kept](#how-long-any-of-it-is-kept)), and a cancelled change can be retried, provided it has not expired in the meantime.

Whatever a retry or a cancel covers, it is recorded as **one** Activity. A retry over a directory that has just come back is a single decision, and a hundred Activities saying so would bury the decision in its own consequences. The Activity is recorded even when nothing matched, so a retry that changed nothing can be told from a retry that never ran.

You are also told where the work is without going looking for it. The **Connected Systems** list carries a Password Synchronisation column showing each system's state, with parked and expired counts beside it, sortable and filterable, including a **Needs attention** filter that cuts across the states. And each Metaverse Object's own page has an administrator-only **Password** tab: Set Password, what is still owed to which of its systems, and what their recent password changes actually did on each one, whether an administrator set them or they were propagated.

That last view is a timeline, grouped by day: one entry per change, marked **Set** or **Propagated**, saying who made it, with a pill per Connected System that names the system and, where it is anything other than delivered, its state (**retrying**, **parked**, **held**, **expired**, **cancelled**). A system that refused the change or is still owed it gets a line beneath in the target's own words, with **Retry** or **Stop trying** on that line; a delivered one needs no words, unless it landed more than a minute after it was asked for, in which case the line says when. A system that took the password but could not apply everything asked of it, such as a change at the next sign-in on a directory that leaves expiry to its own policy, has an amber pill and a line saying what it applied instead. The entry's dot takes the colour of its worst outcome.

It reads from the Activities rather than from the queue, deliberately. A delivered change leaves the queue, so a view built on the queue alone would show a Metaverse Object's failures and none of its successes.

Everything on the tab is scriptable, because a recovery across a directory that has just come back is not a job for a browser:

```powershell
# What needs a person right now
Get-JIMPendingPasswordChange -Summary

# Which systems the parked work is piling up behind
Get-JIMPendingPasswordChange -Status Parked | Group-Object ConnectedSystemName | Select-Object Name, Count

# Once the cause is dealt with: one request, one Activity
Resume-JIMPendingPasswordChange -ConnectedSystemId 3
```

See [PowerShell: Password Synchronisation](../powershell/password-synchronisation.md) for the full set, and the [API reference](../api/index.md) for `GET /api/v1/password-synchronisation/queue` and its `retry` and `cancel` counterparts.

## 🧹 How long any of it is kept

A finished password change is not kept for ever. The built-in **History Retention Cleanup** [Schedule](../configuration/schedules.md#built-in-schedules) runs daily and removes two things once they have had the `History.PasswordEventRetentionPeriod` [Service Setting](../administration/configuration.md#service-settings), which defaults to a year:

- **Queued changes that finished**, whether parked, expired or cancelled. A change still owed to a Connected System is never removed, however old it is.
- **The Activities recording what happened to each change**, including the per-system outcomes behind a Metaverse Object's Password tab.

The two move together on purpose: a Metaverse Object's password history is the outcomes, and a queued change without them says something happened without saying what.

This period is also what bounds how long JIM holds a password. A parked or cancelled change still carries its encrypted password, because both can be retried; shorten the retention period if you would rather JIM stopped holding one sooner. Nothing else ages these out, so a target that refuses passwords would otherwise keep one for every Metaverse Object, for ever.

Each pass says what it removed, on its own Activity, so retention is something you can check rather than assume.

!!! warning "Requiring an encrypted connection means refusing to send"
    A Connected System with **Only send passwords over an encrypted connection** on will not have passwords sent to it over a connection JIM cannot confirm is encrypted. It is on the Connected System's Settings tab, under Passwords, and it governs **every** password JIM sends to that system: the first password on a Connected System Object JIM provisions, one you set by hand, and a synchronised password change alike.

    Nothing is discarded when JIM refuses. Queued password changes, an administrator's reset among them, wait and are delivered once the connection is encrypted or the setting is turned off; Connected System Objects stay owed their first password and get one on the next export. An administrator watching a reset is told at the time that the system is refusing to send, rather than having it go out in the clear.

    Leave it off only where the target genuinely cannot offer an encrypted connection, and understand what that costs: a password sent over an unencrypted one is readable by anyone on the network path.

!!! note "Capturing a password changed in another system is a separate capability"
    Everything here concerns a password change JIM knows about: one an administrator makes, or one sent to JIM's API. Capturing a change made **in** another system, such as a user changing their own password in Active Directory, and replaying it into the others needs a capture agent running on the domain controllers, because no directory will disclose a password when JIM reads from it. When one exists, it will use the same operation as everything above, aimed at every configured system.

## Related

- [Passwords](passwords.md): first passwords, what each system accepts, and setting a password on demand.
- [Connected System Passwords](../configuration/connected-system-passwords.md#password-synchronisation): configuring Password Synchronisation on a system.
- [PowerShell: Password Synchronisation](../powershell/password-synchronisation.md): what is queued, and retrying or cancelling it.
