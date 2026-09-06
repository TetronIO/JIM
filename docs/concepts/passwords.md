# Passwords

A newly provisioned account is not much use until somebody gives it a password. JIM can do that for you, on the accounts it manages, without anyone touching the target system by hand.

It can also set a password on demand: on one account, or the same password across every account a person has.

!!! info "Nothing happens until you ask for it"
    A Connected System is only a candidate if its Connector supports setting passwords. Even then, JIM sets nothing until you configure it: initial passwords are off on every Synchronisation Rule until you switch them on, and every other route here is a deliberate action on a named account. Today the [LDAP Connector](../connectors/jim-ldap-connector.md#setting-passwords) is the Connector that supports it, covering Active Directory, Samba AD, OpenLDAP and generic LDAP directories.

This page covers what JIM does with passwords and why. To actually configure it, follow the links in [Where to go next](#where-to-go-next).

## 🔑 Giving new accounts their first password

Most directories will not let an account be used, or even enabled, until it has a password. Switching on **Initial Password** on an export Synchronisation Rule has JIM set one on every account that rule creates, so the account is complete and enabled from the moment it exists instead of waiting on somebody to do it by hand.

By default that password is different for every account and JIM keeps no copy, so it is not one anybody receives; see [how the person gets their password](#so-how-does-the-person-get-their-password) below. One password for every account is offered as well, and is [not recommended](#one-password-for-every-account).

--8<-- "assets/diagrams/initial-password.svg"

<p class="jim-diagram-caption">The password is set in its own pass at the end of the export run, not as part of creating the account.<span class="jimdg-caption-motion"> Moving dots trace an account through to a delivered password.</span></p>

The setting lives on the Synchronisation Rule rather than on the Connected System because rules are how JIM separates populations: contractors and permanent staff provisioned into the same directory can want different password rules.

### Why it is a separate step

Setting the password happens after the account is created, and cannot fail the export that created it. If it could, JIM would treat the account as never created and try to create it again.

Instead the password is delivered in its own pass at the end of every export run, covering every account on that Connected System still owing one, not just the accounts this run created. **An ordinary export run is therefore the retry.** A directory that was offline, or a permission your service account was missing, is picked up by the next run that was going to happen anyway. There is nothing extra to schedule.

### What you will see afterwards

Every account ends up in one of four states, reported on the export's Activity.

| State | What it means | What you do |
|---|---|---|
| Delivered | The password was set and the account is ready to use. | Nothing. |
| Retrying | JIM could not reach the system, or the account was not visible yet. | Nothing; JIM tries again on the next export run. |
| Parked | The system refused the password itself, for not meeting the rules that apply to that account. Another password generated the same way would be refused for the same reason, so JIM stops rather than spending attempts on the same answer. | Correct the rule's password settings. See below. |
| Expired | The Connected System's initial password window passed without success, a week by default. JIM stops trying, and records that it did rather than quietly forgetting the account. | Set a password on those accounts another way. |

A parked account keeps **the system's own words, unaltered**, because why a directory refused a password is a fact about that directory, and it is the most useful thing you can be shown.

### Getting parked accounts moving again

Parking is not a dead end. **Saving a change to the rule's initial password settings releases every account parked against it**, and they are tried again on that Connected System's next export run. There is nothing to regenerate or invalidate first: a generated password is produced afresh at delivery, and setting a new shared password is itself the change that releases the work, so the retry uses your corrected settings either way. Before you save, the portal tells you how many accounts saving will release, and says nothing at all for an edit that would not change what gets delivered.

You are told where the work is waiting without going looking for it: parked and expired counts appear on the Synchronisation Rules and Connected Systems list pages, on the rule's own Passwords tab, and through `Get-JIMSyncRuleInitialPassword` and `Get-JIMConnectedSystem`. The two counts are shown separately and never added together, because parked work is fixable where it is reported and expired work is not.

### How long JIM keeps trying

An account stays owed its first password for **seven days** by default, after which JIM records the expiry above and stops. That window belongs to the Connected System rather than to the Synchronisation Rule, because what it has to outlast is that system being unavailable, and how long that lasts is a property of the system.

**Raise it before taking a system out of service for longer than the current window.** Every account provisioned while the target is unreachable otherwise expires without a password, and each one then needs a password set by hand. Set it on the Connected System's Settings tab, under **Initial Passwords**, or with `Set-JIMConnectedSystem -Id 1 -InitialPasswordTimeToLive (New-TimeSpan -Days 30)`.

Parked and expired records are kept so you can see what became of an account. They are removed once they have been in that state for the **initial password record retention period** (90 days by default, under Admin > Service Settings), which stops a rule provisioning into a system that refuses its passwords accumulating a record per account for ever. A record still being worked is never removed, however old, and the Activity recording what happened to the account outlives the record either way.

### One password for every account

A generated password nobody can be told is the right answer for getting an account working and the wrong one for the day a new starter arrives. **One password for every account** is the third option under Password Settings: you choose it, JIM sets that same password on every account the rule provisions, and you can put it on an onboarding sheet or read it out.

!!! warning "This option is not recommended"
    Every account the rule provisions shares this password until each person changes it, so anybody who learns of this can sign in as any new starter who has not. Note: the password is stored encrypted and cannot be shown to you again, and it is the only password JIM stores anywhere.

    Leave **Require a change at the next sign-in** switched on: it is what ends each account's share of the password. Any other setting leaves every account the rule provisions on it until somebody changes it by hand.

If you use it, three things are worth knowing:

- **You cannot read it back.** JIM encrypts it and no surface will show it to you again, so keep your own record of it. What JIM will tell you is that one is set and when it last changed, on the rule's Passwords tab and through `Get-JIMSyncRuleInitialPassword`.
- **Change it whenever somebody who knew it leaves.** The date JIM reports is what makes that checkable across every rule at once; there is nothing else that can date a shared password.
- **A password the target would refuse is refused here.** JIM checks it against the policy it discovered when you set it, rather than letting it park every account the rule provisions.

Delivering a generated password to somebody who should have it, by email, is the answer that replaces this one; it is not built yet.

## 🧭 Knowing what each system will accept

A password JIM generates has to satisfy rules JIM did not write, so JIM reads them from the system itself.

Whenever a Connected System's schema is retrieved or refreshed, JIM also reads its password policy and remembers it: minimum length, whether complexity is required and how many character categories that means, password history length, and maximum and minimum password age. Those figures pre-fill the generator, so a compliant password normally needs no configuration from you at all.

**What JIM cannot always find out is worth knowing up front:**

- **Most systems publish nothing.**<br /> Only Active Directory and Samba AD expose a password policy a client can read. Other directories keep their rules in configuration an ordinary connection cannot see, and no cross-vendor standard exists for exposing them. JIM tells you it found nothing rather than implying the system has no rules.
- **A policy can apply to only some accounts.**<br /> Active Directory calls these Fine-Grained Password Policies. Reading them needs privileges your JIM service account should not have, so JIM checks whether any exist rather than reading them, and gives you one of three answers: none, some exist, or it could not tell. "Could not tell" is kept separate from "none" on purpose, because a directory hides what you may not see by returning nothing, which looks identical to there being nothing.
- **A system can enforce rules nothing can discover.**<br /> A custom password filter is exposed over no protocol at all. A password meeting everything JIM read can still be refused.

So treat what JIM discovered as a **floor, not a guarantee**, and read a blank value as "JIM could not find this out", never as "there is no such rule". That is also why the parked state above exists: handling a refusal is part of how this works, not a sign something went wrong.

!!! tip "Check the channel before you rely on it"
    **Check password channel**, on the Connected System's Schema tab, tests the things that usually stop a password being set: whether the connection is encrypted, whether the mechanism JIM needs is available, whether your service account may actually reset passwords in each container, and whether the policy could be read. It sets no password on anything, so it is safe to run against production whenever you like.

    It cannot prove the whole chain, and JIM deliberately offers nothing that does, because the only way to prove it end to end is to reset a real account's password.

## 🔐 Setting a password on demand

Alongside provisioning, you can set a password whenever you need to: the new starter about to sign in for the first time, the account whose provisioning password was refused, the reset that has to happen now.

- **One account.**<br /> Open a Connected System Object and use **Set Password**. The password is masked from the moment it is generated, and **Copy works while it is still masked**, so handing someone their password never means putting it on a screen others can read. Reveal is there for reading one aloud, and hides itself again after thirty seconds.
- **One person, several systems.**<br /> Open a person and the same action lists every account they have that JIM can set a password on. Nothing is selected by default, so a reset in one system never quietly resets the others.

JIM generates passwords in three styles (random characters, words, or a pronounceable password), always from a cryptographic random source, and tells you the length and character categories the result is guaranteed to carry. You can type your own instead. Automation has the same choice, through `-Generate` or `-Password` on the set-password cmdlets and their REST equivalents.

### One password across several systems

Giving somebody four different passwords on their first morning is more work for you and worse for them; they end up on a sticky note. JIM works out a single password that satisfies every selected system at once: the longest minimum length any of them demands, and only the character categories **all** of them count, since a category one system does not recognise cannot help satisfy another's complexity rule.

This is the case where letting JIM generate the password matters most. You cannot see those policies in order to reason about them, and JIM can.

!!! warning "Each system is written to independently"
    There is no transaction across Connected Systems, so a run can end with some accounts changed and others not, leaving the person with a different password where it failed. JIM tells you exactly which, and offers to retry just those, reusing the password already in hand.

    Where a system refused the **password itself**, sending it again would fail identically, so JIM offers a fresh password for every account instead, including the ones that already worked. Replacing it only where it failed would leave the person with two passwords.

    Where JIM could read no policy from a selected system, it says so rather than assuming that system will accept anything. Where no single password could satisfy them all, it refuses before writing anything, rather than handing you one that the first account accepts and the second rejects after the first has already changed.

## 🛡️ How JIM handles passwords safely

**No password value is stored, with one exception you have to choose.** Not in JIM's database, its logs, its Activities, its configuration history, its previews or its search. Each password is generated at the moment it is needed, handed to the Connected System, and dropped.

The exception is a [shared initial password](#one-password-for-every-account) you set on a Synchronisation Rule. That one has to survive until the next account is provisioned, so it is stored, encrypted at rest exactly as a Connected System's credentials are. It is write-only on every surface: no portal page, REST response or cmdlet will return it, and your configuration history records a keyed hash of it, which is enough to show that it changed and when without carrying the password.

A password you explicitly asked JIM to generate for you is a different case and is stored nowhere: it is handed back once, to you, and forgotten. That is why the portal generates one for you on screen but the provisioning path does not; there is nothing kept to look up later.

Every attempt is recorded as an Activity against the account, whether it worked or not, carrying the outcome and the system's own words on a refusal. The Activity records that a password was set. It never records the password.

### So how does the person get their password?

**Not the generated one set during provisioning.** It is different for every account and JIM keeps no copy, so there is nothing for anyone to look up or pass on. Nobody can tell a new starter what it is, including you.

That is deliberate, and it means the initial password is doing a different job from the one it might look like it is doing. Its job is to get the account into a working state: many directories will not enable an account, or let it be used at all, until it holds a password that meets their rules, and an account left sitting with no password while it waits for somebody to get round to it is worth closing off. It is not a password anybody is meant to receive.

There are two ways to give somebody something they can actually sign in with:

- **Set their password when they need it,** using **Set Password** on that account, and hand them the value. Requiring a change at next sign-in (the default) then does what you would expect: they use what you gave them once, and choose their own. This is the right answer for one person at a time.
- **Use [one password for every account](#one-password-for-every-account)** where handing out a password per person is not practical, accepting that every new starter shares it until they change it. Read that section before you do.

!!! warning "Anyone who can set a password can reset any account"
    These actions reset the password on whichever account they are pointed at, including privileged ones, limited only by what the Connected System's service account is allowed to do. Grant the Administrator role accordingly, and restrict the service account's rights to the parts of the directory JIM manages.

    That service account needs the **Reset Password** right on those containers and nothing more; in Active Directory this is separate from write access to attributes. **It does not need to be a Domain Admin, and should not be.** See [Service Account Permissions](../connectors/jim-ldap-connector.md#service-account-permissions).

## ⚙️ Why passwords do not behave like other data

Everything else JIM manages flows through the Metaverse: imported from a source system, held centrally, evaluated by your Synchronisation Rules, queued as a Pending Export, and written out. That machinery remembers values, compares them, shows them to you, and retries them. All four are the wrong thing to do with a credential.

Passwords therefore travel their own path, which never touches any of it.

| | Ordinary attributes | Passwords |
|---|---|---|
| Held centrally in the Metaverse | ✅ | ❌ |
| Queued as a Pending Export | ✅ | ❌ written straight to the system |
| Kept in configuration and object history | ✅ | ❌ a shared initial password is recorded as a keyed hash, never a value |
| Read back when JIM imports | ✅ | ❌ |
| Retried automatically | ✅ | Only an initial password, and only while a retry could help |

The practical consequence is that **attributes holding credentials cannot be managed as attributes at all.** `unicodePwd`, `userPassword` and their relatives cannot be imported and cannot be chosen in an Attribute Flow; JIM does not offer them. If an earlier version of your deployment had selected one, the next schema refresh deselects and locks it rather than deleting it, so any Synchronisation Rule referring to it stays intact. See [Credential attributes are never managed](../configuration/connected-systems.md#credential-attributes-are-never-managed).

It also means JIM writes passwords the way each directory expects rather than writing an attribute: for Active Directory it sets `unicodePwd` with the correct encoding, and elsewhere it uses the standard LDAP Password Modify operation. It never writes a password attribute directly, because directories store a directly written value exactly as supplied, which would leave the password readable in the directory.

## 🔁 Password Synchronisation

Everything above concerns setting a password on one account, at the moment you ask. Password Synchronisation is the other half: one password change reaching **every** system that person has an account in, durably, without you standing over it.

--8<-- "assets/diagrams/password-synchronisation.svg"

<p class="jim-diagram-caption">Each system gets its own queued change, so none of them waits on another and one unavailable system cannot fail the rest.<span class="jimdg-caption-motion"> Moving dots trace one password change fanning out.</span></p>

You configure it per Connected System, on the **Passwords** tab of the Connected System, and it appears only on systems whose connector can set passwords at all. Two settings, and one deliberate separation between them:

- **The configuration** says which Object Type holds the accounts, how many delivery attempts to make before JIM stops and asks you to look, how long to wait before the first retry, and whether to refuse to transmit over a connection JIM cannot confirm is encrypted.
- **The enable toggle** is separate from the configuration existing, so you can set a system up ahead of a change window and switch it on during one. A configured system that is switched **off** does not discard password changes: they accumulate, and switching it on delivers what accumulated.

That is also why there is no way to remove a configuration, only to disable it. Removing one would throw away everything queued against it.

How long a change waits before JIM gives up on it is the Connected System's **initial password time to live** setting, shared with initial password provisioning: the question both are asking is how long that system may be unavailable before JIM stops trying, and the answer is a property of the system.

### ▶️ Starting a synchronised password change

JIM has two ways to give somebody a password, and they answer different questions.

| | **Set Password** | **Synchronise Password** |
|---|---|---|
| Answers | "Change this person's password in the systems I choose" | "This person's password changed; every system should hold it" |
| Reaches | The accounts you tick | Every Connected System enabled for Password Synchronisation |
| When | Immediately, while you wait | Recorded now, delivered on its own clock |
| If a system is down | That account fails, and you are told | Queued and retried until it works or the window closes |
| Told to you | Success or failure per account | Which systems it was queued for |

Use **Set Password** when you are choosing the password yourself and applying it to systems you name: onboarding somebody, or putting right an account whose password was refused or forgotten. Use **Synchronise Password** when they have already changed their own password somewhere and the rest should catch up.

Both are on the Metaverse Object's Actions tab, and both are available to automation:

```powershell
# Change the password on chosen accounts, now
Set-JIMMetaverseObjectPassword -Id $id -ConnectedSystemId 3 -Password $password

# Propagate a password change everywhere it belongs
Sync-JIMMetaverseObjectPassword -Id $id -Password $password
```

Over REST, that is `POST /api/v1/synchronisation/connected-systems/{connectedSystemId}/connector-space/{csoId}/password` and `POST /api/v1/metaverse/objects/{id}/password` respectively. Every endpoint that accepts a password refuses the request unless JIM can confirm the connection is encrypted; if TLS terminates at a reverse proxy, set `JIM_TRUSTED_PROXIES` so JIM reads the forwarded scheme rather than the hop it can see.

### 📬 How a password change reaches a system

A password change is recorded first and delivered afterwards, never in the same breath. The person changing their password must not be held waiting on a directory, and their new password must not fail to take because one of the systems they have an account in happens to be down. So JIM writes one queued change per target system, encrypted, and returns; delivery runs on its own.

What happens to a queued change:

- **It is delivered, and disappears.** Nothing is kept once the target has the password: there is no value worth retaining and every reason not to.
- **It is retried.** A target that was unreachable, or that failed in a way another attempt may resolve, gets one. Each wait is twice as long as the one before it, starting from the backoff you configured, and never longer than the time the change has left.
- **It is parked, and waits for you.** A target that *refused* the password, or that cannot do what was asked at all, will refuse it identically next time; JIM stops rather than burning the attempts. So does a change that has used all of them. Parked work is released, and tried again, the moment you change what would be delivered to that system: switching Password Synchronisation on, or correcting a setting.
- **It is held, because the system is switched off.** Password Synchronisation being switched off on a Connected System does not stop changes being recorded for it; it stops them being sent. They accumulate, shown as **Held**, and switching the system back on delivers all of them without anything else being done. Nothing about a held change is attempted while it waits, so it does not consume attempts and does not appear in the due count. It still expires on time, which is what bounds how long a change window can last before the passwords made during it are lost.
- **It expires.** A change that outlives its time to live is retired with its last failure recorded, rather than delivering a password the person may have changed twice since.

A change for someone who changes their password again before the first one is delivered replaces the first, rather than queueing behind it. Only the newest password is ever sent.

Delivery is a Password Delivery task in the Operations queue, so a pass is visible while it runs and its outcome is recorded as an Activity like any other work. A pass is raised when a password change is queued, when you enable Password Synchronisation on a system (to deliver what accumulated), and by JIM itself when a retry falls due. A system that is switched off is never swept for: its changes are held rather than due, so no pass is raised on their account until you switch it on.

### 🔎 Watching the queue

Delivery works on its own, which is exactly why you need somewhere to look when it does not. The **Passwords** tab of **Administration > Operations** lists every change on its way to a Connected System, one row per person per system, with what the target said about it. It sits beside the Queue, History and Schedules tabs because it answers the same question they do: what JIM is doing, and what it has stopped doing. The tab is badged with how many changes are waiting on a person (parked plus expired), so a backlog is visible from anywhere on the Operations page.

It never shows a password, and cannot: the queued value is encrypted in the database and has no representation on any page, in any API response, or in any log line.

Four counts sit above the list:

- **Waiting**<br /> Changes JIM still intends to deliver. The second line says how many of those a delivery pass would attempt right now; the rest are waiting out a retry backoff, or are held because their Connected System is switched off. A large waiting count with nothing due is a queue working through its backoffs, or one waiting on a system to be switched back on. A large due count is a queue that is not being drained.
- **Parked**<br /> The target refused them, or they ran out of attempts. These wait on you.
- **Expired**<br /> They outlived their time to live. The password each carried is gone, so nothing can deliver them now.
- **Cancelled**<br /> You stopped them. Counted rather than hidden, because that person's password is still divergent on that system and the count is the only thing that says so.

Filter by Connected System, by state, or by how the last attempt failed, and search by person or system. Two actions apply to whatever the filters are currently showing, as well as to a single row:

- **Retry**<br /> Makes matching changes due immediately and raises a delivery pass. This is what you run once the reason a directory was refusing passwords has been dealt with. It applies to waiting, parked and cancelled changes; an expired one is left alone, because there is no password left to send.
- **Cancel**<br /> Stops JIM delivering them. The changes stay, marked **Cancelled**, recording who cancelled them and when.

!!! note "Cancelling records an outcome; it does not erase one"
    A cancelled change is kept for the same reason an expired one is: that person's password on that system is now out of step with the rest, and deleting it would leave you believing your systems agree when they do not. Retention removes cancelled changes on the same schedule as any other finished change (see [How long any of it is kept](#-how-long-any-of-it-is-kept)), and a cancelled change can be retried, provided it has not expired in the meantime.

Whatever a retry or a cancel covers, it is recorded as **one** Activity. A retry over a directory that has just come back is a single decision, and a hundred Activities saying so would bury the decision in its own consequences. The Activity is recorded even when nothing matched, so a retry that changed nothing can be told from a retry that never ran.

You are also told where the work is without going looking for it. The **Connected Systems** list carries a Password Synchronisation column showing each system's state, with parked and expired counts beside it, sortable and filterable, including a **Needs attention** filter that cuts across the states. And each person's own page has an administrator-only **Password Synchronisation** tab: what is still owed to which of their systems, and what their recent password changes actually did on each one.

That last view reads from the Activities rather than from the queue, deliberately. A delivered change leaves the queue, so a view built on the queue alone would show a person's failures and none of their successes.

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

### 🧹 How long any of it is kept

A finished password change is not kept for ever. The built-in **History Retention Cleanup** [Schedule](../configuration/schedules.md#built-in-schedules) runs daily and removes two things once they have had the `History.PasswordEventRetentionPeriod` [Service Setting](../administration/configuration.md#service-settings), which defaults to a year:

- **Queued changes that finished**, whether parked, expired or cancelled. A change still owed to a Connected System is never removed, however old it is.
- **The Activities recording what happened to each change**, including the per-system outcomes behind a person's Password Synchronisation tab.

The two move together on purpose: a person's password history is the outcomes, and a queued change without them says something happened without saying what.

This period is also what bounds how long JIM holds a password. A parked or cancelled change still carries its encrypted password, because both can be retried; shorten the retention period if you would rather JIM stopped holding one sooner. Nothing else ages these out, so a target that refuses passwords would otherwise keep one for every person, for ever.

Each pass says what it removed, on its own Activity, so retention is something you can check rather than assume.

!!! warning "Requiring an encrypted connection means refusing to send"
    A Connected System with **Only send passwords over an encrypted connection** on will not have passwords sent to it over a connection JIM cannot confirm is encrypted. It is on the Connected System's Settings tab, under Passwords, and it governs **every** password JIM sends to that system: the first password on an account JIM provisions, one you set by hand, and a synchronised password change alike.

    Nothing is discarded when JIM refuses. Queued password changes wait and are delivered once the connection is encrypted or the setting is turned off; accounts stay owed their first password and get one on the next export; an administrator setting a password by hand is told outright, at the time, rather than having it go out in the clear.

    Leave it off only where the target genuinely cannot offer an encrypted connection, and understand what that costs: a password sent over an unencrypted one is readable by anyone on the network path.

!!! note "Capturing a password changed in another system is a separate capability"
    Everything here concerns a password change JIM knows about: one an administrator makes, or one sent to JIM's API. Capturing a change made **in** another system, such as a user changing their own password in Active Directory, and replaying it into the others needs a capture agent running on the domain controllers, because no directory will disclose a password when JIM reads from it.

## Where to go next

| To | See |
|---|---|
| Switch on initial passwords for a rule | [Synchronisation Rules: Initial password](../configuration/synchronisation-rules.md#initial-password) |
| See a discovered policy, or run the channel check | [Connected Systems: Password policy and the password channel](../configuration/connected-systems.md#password-policy-and-the-password-channel) |
| Set a password on one account, or on a person | [Connected Systems: Setting the password on one account](../configuration/connected-systems.md#setting-the-password-on-one-account) |
| Configure Password Synchronisation on a system | [Connected Systems: Password Synchronisation](../configuration/connected-systems.md#password-synchronisation) |
| Directory specifics: encryption, mechanisms, permissions | [LDAP Connector: Setting Passwords](../connectors/jim-ldap-connector.md#setting-passwords) |
| See what is queued, and retry or cancel it | [PowerShell: Password Synchronisation](../powershell/password-synchronisation.md) |
| Do any of it from a script | [PowerShell: Connected Systems](../powershell/connected-systems.md#set-jimconnectedsystemobjectpassword), [Metaverse](../powershell/metaverse.md#set-jimmetaverseobjectpassword), [Synchronisation Rules](../powershell/synchronisation-rules.md#initial-password) |
